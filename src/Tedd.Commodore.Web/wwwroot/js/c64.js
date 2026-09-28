// c64.js - browser glue for the Tedd.CommodoreEmulator C64 emulator.
//
// This ES module is imported from C# with JSHost.ImportAsync("c64", <url>) and bound through the
// [JSImport] declarations in Interop/C64Js.cs. It is deliberately framework-free so any web host
// (Blazor, a plain .NET WebAssembly browser app, ...) can reuse it. Responsibilities:
//
//   video  : attachCanvas / presentFrame   - one RGBA frame (384x272) per call, drawn with putImageData
//   audio  : audioStart / audioWrite / ... - AudioWorklet fed with 16-bit PCM chunks (see c64-audio-worklet.js)
//   input  : attachInput / detachInput     - keyboard events on the document while the screen has focus
//   loop   : startLoop / stopLoop          - requestAnimationFrame calling the [JSExport] BrowserLoop.Tick
//   misc   : fullscreen, PNG download, drag & drop, localStorage
//
// Byte buffers arrive as MemoryView objects (a view over the WebAssembly heap that is only valid during the
// call); slice() copies them out.

let canvas = null;
let ctx = null;
let imageData = null;
let imageBytes = null;   // Uint8Array view over imageData's buffer (MemoryView.copyTo needs a Uint8Array)

let audioCtx = null;
let workletNode = null;

let loopHandle = 0;
let tickFn = null;
let inputHandlers = null;

const runtimeVersion = "1.0";

// ---------------------------------------------------------------- video

export function attachCanvas(canvasId, width, height) {
    canvas = document.getElementById(canvasId);
    if (!canvas) throw new Error("canvas '" + canvasId + "' not found");
    canvas.width = width;
    canvas.height = height;
    ctx = canvas.getContext("2d", { alpha: false, desynchronized: true });
    const pixels = new Uint8ClampedArray(width * height * 4);
    imageData = new ImageData(pixels, width, height);
    imageBytes = new Uint8Array(pixels.buffer);
    ctx.fillStyle = "#000";
    ctx.fillRect(0, 0, width, height);
}

export function presentFrame(rgba) {
    if (!ctx) return;
    // MemoryView.copyTo is the cheapest path: one copy from the WebAssembly heap straight into the ImageData's
    // buffer (through a Uint8Array view of it, which is what copyTo accepts). Fall back to slice()+set().
    if (typeof rgba.copyTo === "function") rgba.copyTo(imageBytes);
    else imageData.data.set(rgba.slice());
    ctx.putImageData(imageData, 0, 0);
}

// ---------------------------------------------------------------- audio

export async function audioStart() {
    if (!audioCtx) {
        const Ctor = window.AudioContext || window.webkitAudioContext;
        audioCtx = new Ctor({ latencyHint: "interactive" });
        await audioCtx.audioWorklet.addModule(new URL("./c64-audio-worklet.js", import.meta.url));
        workletNode = new AudioWorkletNode(audioCtx, "c64-pcm", {
            numberOfInputs: 0,
            numberOfOutputs: 1,
            outputChannelCount: [1],
            processorOptions: { sampleRate: audioCtx.sampleRate }
        });
        workletNode.connect(audioCtx.destination);
    }
    await ensureAudioRunning();
    return audioCtx.sampleRate;
}

// resume() only succeeds inside a user gesture and its promise may stay pending without one, so never wait
// for it longer than a moment; the emulator runs regardless and the next key/pointer gesture retries.
async function ensureAudioRunning() {
    if (!audioCtx || audioCtx.state === "running") return;
    try {
        await Promise.race([audioCtx.resume(), new Promise(r => setTimeout(r, 1500))]);
    } catch (e) {
        console.warn("AudioContext.resume failed", e);
    }
}

export function audioState() {
    return audioCtx ? audioCtx.state : "none";
}

// pcm16: MemoryView over little-endian signed 16-bit mono samples
export function audioWrite(pcm16) {
    if (!workletNode) return;
    const bytes = pcm16.slice();
    workletNode.port.postMessage({ t: "pcm", buf: bytes.buffer }, [bytes.buffer]);
}

export function audioClear() {
    if (workletNode) workletNode.port.postMessage({ t: "clear" });
}

export async function audioSuspend() {
    if (audioCtx && audioCtx.state === "running") await audioCtx.suspend();
}

export async function audioResume() {
    if (audioCtx && audioCtx.state !== "running") await audioCtx.resume();
}

// ---------------------------------------------------------------- input

// onKey(code, isDown) returns true when the key is bound (the event is then swallowed);
// onBlur() is raised when the focus element loses focus or the window blurs (release everything).
export function attachInput(focusElementId, onKey, onBlur) {
    detachInput();
    const el = document.getElementById(focusElementId);
    if (!el) throw new Error("element '" + focusElementId + "' not found");
    const hasFocus = () => document.activeElement === el || el.contains(document.activeElement);
    const keydown = e => {
        if (!hasFocus()) return;
        ensureAudioRunning();          // a key press is a user gesture: good moment to unblock audio
        if (e.repeat) { e.preventDefault(); return; }
        if (onKey(e.code, true)) e.preventDefault();
    };
    const keyup = e => {
        if (!hasFocus()) return;
        if (onKey(e.code, false)) e.preventDefault();
    };
    const blur = () => onBlur();
    const pointerdown = () => ensureAudioRunning();
    document.addEventListener("keydown", keydown);
    document.addEventListener("keyup", keyup);
    el.addEventListener("blur", blur);
    el.addEventListener("pointerdown", pointerdown);
    window.addEventListener("blur", blur);
    inputHandlers = { el, keydown, keyup, blur, pointerdown };
}

export function detachInput() {
    if (!inputHandlers) return;
    document.removeEventListener("keydown", inputHandlers.keydown);
    document.removeEventListener("keyup", inputHandlers.keyup);
    inputHandlers.el.removeEventListener("blur", inputHandlers.blur);
    inputHandlers.el.removeEventListener("pointerdown", inputHandlers.pointerdown);
    window.removeEventListener("blur", inputHandlers.blur);
    inputHandlers = null;
}

export function focusElement(id) {
    const el = document.getElementById(id);
    if (el) el.focus({ preventScroll: true });
}

// ---------------------------------------------------------------- frame loop

// Resolves the [JSExport] Tick method of the .NET side. The Blazor runtime exposes getDotnetRuntime(0);
// a fallback callback can be supplied by the host for other runtimes.
async function resolveTick(fallback) {
    try {
        if (typeof globalThis.getDotnetRuntime === "function") {
            const runtime = await globalThis.getDotnetRuntime(0);
            const exports = await runtime.getAssemblyExports("Tedd.Commodore.Web");
            const fn = exports?.Tedd?.Commodore?.Web?.Interop?.BrowserLoop?.Tick;
            if (typeof fn === "function") return fn;
        }
    } catch (e) {
        console.warn("getAssemblyExports failed, using callback", e);
    }
    return fallback;
}

export async function startLoop(fallbackTick) {
    if (loopHandle) return;
    tickFn = await resolveTick(fallbackTick);
    if (!tickFn) throw new Error("no tick function available");
    const step = t => {
        loopHandle = requestAnimationFrame(step);
        try { tickFn(t); }
        catch (e) { console.error("emulator tick failed", e); stopLoop(); }
    };
    loopHandle = requestAnimationFrame(step);
}

export function stopLoop() {
    if (loopHandle) cancelAnimationFrame(loopHandle);
    loopHandle = 0;
}

export function now() {
    return performance.now();
}

// ---------------------------------------------------------------- misc

export function requestFullscreen(id) {
    const el = document.getElementById(id);
    if (!el) return;
    if (document.fullscreenElement) document.exitFullscreen();
    else if (el.requestFullscreen) el.requestFullscreen();
    else if (el.webkitRequestFullscreen) el.webkitRequestFullscreen();
}

export function downloadCanvas(canvasId, fileName) {
    const c = document.getElementById(canvasId);
    if (!c) return;
    const a = document.createElement("a");
    a.href = c.toDataURL("image/png");
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
}

export function downloadBytes(bytes, fileName, mime) {
    const blob = new Blob([bytes], { type: mime || "application/octet-stream" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// onFile(name) is called for every dropped file; the receiver fetches the bytes with takeDroppedFile(name)
// (.NET callbacks cannot take arrays as arguments, so the payload is handed over in a second step).
const droppedFiles = new Map();

export function attachDrop(elementId, onFile) {
    const el = document.getElementById(elementId);
    if (!el) return;
    el.addEventListener("dragover", e => { e.preventDefault(); e.dataTransfer.dropEffect = "copy"; el.classList.add("drop-hover"); });
    el.addEventListener("dragleave", () => el.classList.remove("drop-hover"));
    el.addEventListener("drop", async e => {
        e.preventDefault();
        el.classList.remove("drop-hover");
        for (const file of e.dataTransfer.files) {
            const buf = await file.arrayBuffer();
            droppedFiles.set(file.name, new Uint8Array(buf));
            onFile(file.name);
        }
    });
}

export function takeDroppedFile(name) {
    const data = droppedFiles.get(name);
    droppedFiles.delete(name);
    return data || null;
}

export function storageGet(key) {
    try { return localStorage.getItem(key); } catch { return null; }
}

export function storageSet(key, value) {
    try { localStorage.setItem(key, value); return true; } catch { return false; }
}

export function storageRemove(key) {
    try { localStorage.removeItem(key); } catch { /* ignore */ }
}

export function isTouchDevice() {
    return ("ontouchstart" in window) || navigator.maxTouchPoints > 0;
}

export function version() {
    return runtimeVersion;
}
