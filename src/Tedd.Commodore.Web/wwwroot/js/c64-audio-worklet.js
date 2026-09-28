// c64-audio-worklet.js - AudioWorklet processor that plays the SID output.
//
// The emulator posts { t: "pcm", buf: ArrayBuffer } messages containing little-endian signed 16-bit mono
// samples at the AudioContext sample rate (one message per emulated frame, ~20 ms). They are converted to
// float and kept in a ring buffer; process() drains 128 samples per render quantum. Underruns produce
// silence (and the buffer re-primes before playback resumes, so a single hiccup does not turn into a long
// crackle); when the emulator gets ahead (tab throttling, warp) the oldest samples are dropped so latency
// stays bounded.

class C64PcmProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();
        const rate = (options.processorOptions && options.processorOptions.sampleRate) || sampleRate;
        this.capacity = Math.max(8192, Math.round(rate * 2));   // two seconds
        this.ring = new Float32Array(this.capacity);
        this.readPos = 0;
        this.writePos = 0;
        this.count = 0;
        this.primeLevel = Math.round(rate * 0.06);              // start playing once 60 ms are queued
        this.maxLevel = Math.round(rate * 0.30);                // never queue more than 300 ms
        this.trimLevel = Math.round(rate * 0.10);               // ...and trim back to 100 ms when exceeded
        this.primed = false;
        this.underruns = 0;
        this.port.onmessage = e => this.onMessage(e.data);
    }

    onMessage(msg) {
        if (!msg) return;
        if (msg.t === "clear") {
            this.readPos = this.writePos = this.count = 0;
            this.primed = false;
            return;
        }
        if (msg.t === "pcm" && msg.buf) {
            const pcm = new Int16Array(msg.buf);
            this.push(pcm);
        }
    }

    push(pcm) {
        const n = pcm.length;
        if (this.count + n > this.maxLevel) {
            // Too far ahead: drop the oldest samples so that only trimLevel remain (plus the new chunk).
            const drop = this.count - this.trimLevel;
            if (drop > 0) {
                this.readPos = (this.readPos + drop) % this.capacity;
                this.count -= drop;
            }
        }
        for (let i = 0; i < n; i++) {
            this.ring[this.writePos] = pcm[i] / 32768;
            this.writePos = (this.writePos + 1) % this.capacity;
        }
        if (this.count + n > this.capacity) {
            // ring overflow (should not happen with maxLevel < capacity): keep the newest samples
            this.readPos = this.writePos;
            this.count = this.capacity;
        } else {
            this.count += n;
        }
        if (!this.primed && this.count >= this.primeLevel) this.primed = true;
    }

    process(inputs, outputs) {
        const out = outputs[0][0];
        const n = out.length;
        if (!this.primed || this.count < n) {
            out.fill(0);
            if (this.primed) { this.primed = false; this.underruns++; }
            return true;
        }
        for (let i = 0; i < n; i++) {
            out[i] = this.ring[this.readPos];
            this.readPos = (this.readPos + 1) % this.capacity;
        }
        this.count -= n;
        return true;
    }
}

registerProcessor("c64-pcm", C64PcmProcessor);
