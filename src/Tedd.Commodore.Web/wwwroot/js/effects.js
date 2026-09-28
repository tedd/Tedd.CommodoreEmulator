// effects.js - presentation-only page effects for the Tedd.CommodoreEmulator site.
//
// Loaded as a plain (non-module) script from index.html after the vendored GSAP bundles, so
// window.gsap / ScrollTrigger / ScrollToPlugin / TextPlugin / SplitText are already defined here.
// Nothing in this file touches the emulator: it only animates the page around it, and it
// deliberately never puts a transform on an ancestor of #emu-screen (that would break fullscreen).
//
// Because the page is rendered by Blazor WebAssembly, the DOM this file animates does not exist when
// the script runs. The flow is:
//
//   1. immediately  : add "fx-on fx-pre" to <html>. The CSS in css/app.css hides (opacity: 0) exactly
//                     the elements animated in below, so there is no flash of un-animated content.
//                     If GSAP is missing or the user prefers reduced motion the classes are never
//                     added and the page stays completely static - the site works without any of this.
//   2. MutationObserver waits for Blazor to render <section class="hero">.
//   3. init()       : hand the initial states to GSAP, drop "fx-pre", play.
//   4. safety timer : if step 2/3 never happens, drop "fx-pre" anyway so nothing stays invisible.
//
// Effects: hero headline split into characters, typed tagline, animated C64 raster-bar + starfield
// background canvas, scroll-triggered reveals, per-section nav highlighting, scroll progress bar,
// card tilt/glow, magnetic buttons and eased anchor scrolling.
(function () {
    "use strict";

    var g = window.gsap;
    if (!g || !window.ScrollTrigger) return;                        // vendor scripts missing -> static page
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;

    g.registerPlugin(ScrollTrigger, ScrollToPlugin, TextPlugin, SplitText);
    ScrollTrigger.config({ ignoreMobileResize: true });              // mobile URL bar show/hide is not a resize

    var root = document.documentElement;
    var canHover = window.matchMedia("(hover: hover) and (pointer: fine)").matches;

    // A few C64 palette entries, used for the raster bars and the logo colour cycle.
    var PALETTE = ["#6c5eb5", "#70a4b2", "#9ad284", "#b8c76f", "#6f3d86", "#9a6759"];
    var LOGO_BG = "#352879";                                         // C64 blue, matches .site-header .logo

    root.classList.add("fx-on", "fx-pre");
    var safety = setTimeout(function () { root.classList.remove("fx-pre"); }, 8000);

    whenRendered(function () {
        clearTimeout(safety);
        try { init(); }
        finally { root.classList.remove("fx-pre"); }
    });

    // ---------------------------------------------------------------- bootstrap

    function whenRendered(cb) {
        if (document.querySelector(".site-main .hero")) { cb(); return; }
        var mo = new MutationObserver(function () {
            if (!document.querySelector(".site-main .hero")) return;
            mo.disconnect();
            cb();
        });
        mo.observe(document.body, { childList: true, subtree: true });
    }

    function $(sel, ctx) { return (ctx || document).querySelector(sel); }
    function $$(sel, ctx) { return Array.prototype.slice.call((ctx || document).querySelectorAll(sel)); }

    // The layout viewport. Not window.innerWidth/innerHeight: those include the scrollbar, and some
    // embedded browser hosts report them as 0.
    function vw() { return root.clientWidth || window.innerWidth || 0; }
    function vh() { return root.clientHeight || window.innerHeight || 0; }

    function init() {
        var header = $(".site-header");
        var headerH = header ? header.offsetHeight : 56;

        background();
        heroIntro();
        reveals();
        headerChrome(header, headerH);
        anchorScrolling(headerH);
        cardInteractions();
        magneticButtons();
        emulatorArrival();

        // <details class="panel"> in the emulator changes the page height; so does going fullscreen.
        // (Deliberately no MutationObserver here: the emulator rewrites its status line every frame.)
        document.addEventListener("toggle", refreshSoon, true);
        document.addEventListener("fullscreenchange", refreshSoon);
        window.addEventListener("load", refreshSoon);
    }

    var refreshTimer = 0;
    function refreshSoon() {
        clearTimeout(refreshTimer);
        refreshTimer = setTimeout(function () { ScrollTrigger.refresh(); }, 250);
    }

    function debounce(fn, ms) {
        var timer = 0;
        return function () {
            clearTimeout(timer);
            timer = setTimeout(fn, ms);
        };
    }

    // ---------------------------------------------------------------- hero

    function heroIntro() {
        var hero = $(".hero");
        var h1 = $(".hero h1");
        var tagline = $(".hero .tagline");
        var lead = $(".hero .lead");
        var cta = $(".hero .cta");
        if (!hero || !h1) return;

        // A slow band of light sweeping down the hero, like a CRT scan. It repeats forever, so it is
        // paused whenever the hero is off screen - an endless tween would otherwise keep GSAP's
        // requestAnimationFrame loop alive for the whole visit.
        var scan = document.createElement("span");
        scan.className = "fx-scan";
        scan.setAttribute("aria-hidden", "true");
        hero.appendChild(scan);
        var scanTween = g.fromTo(scan, { yPercent: -140 },
            { yPercent: 140, duration: 7, ease: "none", repeat: -1, repeatDelay: 5 });
        ScrollTrigger.create({
            trigger: hero, start: "top bottom", end: "bottom top",
            onToggle: function (self) { if (self.isActive) { scanTween.play(); } else { scanTween.pause(); } }
        });

        var chars = SplitText.create(h1, { type: "chars", charsClass: "fx-char" }).chars;

        // The tagline is typed out, so start it empty and keep the text for TextPlugin.
        var taglineText = tagline ? tagline.textContent.trim() : "";
        var caret = null;
        if (tagline) {
            tagline.textContent = "";
            caret = document.createElement("span");
            caret.className = "fx-caret";
            caret.setAttribute("aria-hidden", "true");
            caret.textContent = "█";
            tagline.appendChild(caret);
        }

        // Initial states, applied while <html> still has .fx-pre (everything below is still opacity: 0).
        g.set([h1, tagline, lead, cta].filter(Boolean), { opacity: 1 });
        g.set(chars, { yPercent: 110, opacity: 0, color: "#8f83d6" });
        g.set([lead, cta].filter(Boolean), { opacity: 0, y: 24 });

        var tl = g.timeline({ defaults: { ease: "power3.out" }, delay: 0.15 });
        tl.to(chars, { yPercent: 0, opacity: 1, duration: 0.7, stagger: 0.035 })
          .to(chars, { color: "#e6e9ee", duration: 0.5, stagger: 0.035, clearProps: "color" }, 0.25);

        if (tagline) {
            // TextPlugin replaces the element's content, so the caret is re-appended after every step.
            tl.to(tagline, {
                duration: Math.max(0.8, taglineText.length * 0.032), ease: "none",
                text: { value: taglineText, delimiter: "" },
                onUpdate: function () { if (caret) tagline.appendChild(caret); },
                onComplete: function () { if (caret) tagline.appendChild(caret); }
            }, "-=0.35");
        }
        if (caret) tl.to(caret, { opacity: 0, duration: 0.4 }, ">+0.8");
        tl.to([lead, cta].filter(Boolean), { opacity: 1, y: 0, duration: 0.7, stagger: 0.12 }, "-=1.4");

        // Hero content drifts up and dims over the first screenful of scrolling. Anchored to the scroll
        // position (start: 0) rather than to the hero's own edges, so it always starts untouched at the top
        // of the page - however tall the window is. Built only once the intro has finished: a scrubbed tween
        // records its start values when it is created, so creating it up front would pin the lead and the
        // buttons at the hidden state the intro starts from.
        tl.eventCallback("onComplete", function () {
            g.to([h1, tagline, lead, cta].filter(Boolean), {
                y: -40, opacity: 0.2, ease: "none",
                scrollTrigger: { start: 0, end: function () { return vh() * 0.7; }, scrub: true }
            });
        });
    }

    // ---------------------------------------------------------------- scroll reveals

    function reveals() {
        // Section headings get a raster-coloured rule that wipes in under them.
        $$(".site-main section > h2").forEach(function (h2) {
            var rule = document.createElement("span");
            rule.className = "fx-rule";
            rule.setAttribute("aria-hidden", "true");
            h2.parentNode.insertBefore(rule, h2.nextSibling);

            g.set(h2, { opacity: 0, y: 24 });
            g.set(rule, { scaleX: 0, transformOrigin: "left center" });
            g.timeline({ scrollTrigger: { trigger: h2, start: "top 88%" }, defaults: { ease: "power3.out" } })
                .to(h2, { opacity: 1, y: 0, duration: 0.6 })
                .to(rule, { scaleX: 1, duration: 0.8, ease: "power2.out" }, "-=0.35");
        });

        // Cards come in a few at a time, in the order they scroll into view.
        var cards = $$(".site-main .card");
        g.set(cards, { opacity: 0, y: 42 });
        ScrollTrigger.batch(cards, {
            start: "top 90%",
            onEnter: function (batch) {
                g.to(batch, { opacity: 1, y: 0, duration: 0.7, stagger: 0.1, ease: "power3.out", overwrite: true });
            }
        });

        // Key tables: rows tick in like a listing scrolling up the screen.
        $$(".site-main table.keys").forEach(function (table) {
            var rows = $$("tr", table);
            g.set(rows, { opacity: 0, x: -14 });
            g.to(rows, {
                opacity: 1, x: 0, duration: 0.35, stagger: 0.035, ease: "power2.out",
                scrollTrigger: { trigger: table, start: "top 85%" }
            });
        });

        // Intro paragraphs that sit directly in a section (not inside a card, not in the hero).
        var leads = $$(".site-main section > p").filter(function (p) { return !p.closest(".hero"); });
        g.set(leads, { opacity: 0, y: 18 });
        ScrollTrigger.batch(leads, {
            start: "top 92%",
            onEnter: function (batch) {
                g.to(batch, { opacity: 1, y: 0, duration: 0.55, stagger: 0.08, ease: "power2.out", overwrite: true });
            }
        });
    }

    // ---------------------------------------------------------------- header: progress, condense, active link

    function headerChrome(header, headerH) {
        if (!header) return;

        var bar = document.createElement("div");
        bar.className = "fx-progress";
        bar.setAttribute("aria-hidden", "true");
        bar.appendChild(document.createElement("i"));
        header.appendChild(bar);
        g.fromTo(bar.firstChild, { scaleX: 0 }, {
            scaleX: 1, ease: "none", transformOrigin: "left center",
            scrollTrigger: { start: 0, end: "max", scrub: 0.3 }
        });

        ScrollTrigger.create({
            start: 40,
            onToggle: function (self) { header.classList.toggle("fx-scrolled", self.isActive); }
        });

        // Highlight the nav link of whichever section currently owns the top of the viewport.
        $$(".site-header nav a[href^='#']").forEach(function (link) {
            var section = document.getElementById(link.getAttribute("href").slice(1));
            if (!section) return;
            ScrollTrigger.create({
                trigger: section,
                start: "top " + (headerH + 40) + "px",
                end: "bottom " + (headerH + 40) + "px",
                onToggle: function (self) { link.classList.toggle("active", self.isActive); }
            });
        });

        // The C64 badge cycles through the palette while the pointer is on it.
        var logo = $(".site-header .logo");
        if (logo) {
            var cycle = g.timeline({ paused: true, repeat: -1 });
            PALETTE.forEach(function (c) { cycle.to(logo, { backgroundColor: c, duration: 0.35, ease: "none" }); });
            cycle.to(logo, { backgroundColor: LOGO_BG, duration: 0.35, ease: "none" });
            logo.addEventListener("pointerenter", function () { cycle.play(); });
            logo.addEventListener("pointerleave", function () {
                cycle.pause();
                g.to(logo, { backgroundColor: LOGO_BG, duration: 0.3 });
            });
        }
    }

    // ---------------------------------------------------------------- eased anchor scrolling

    function anchorScrolling(headerH) {
        document.addEventListener("click", function (e) {
            var a = e.target.closest ? e.target.closest("a[href^='#']") : null;
            if (!a || e.defaultPrevented || e.metaKey || e.ctrlKey || e.shiftKey || e.button !== 0) return;
            var id = a.getAttribute("href").slice(1);
            var target = id ? document.getElementById(id) : null;
            if (!target) return;
            e.preventDefault();
            g.to(window, {
                duration: 0.9, ease: "power2.inOut",
                scrollTo: { y: target, offsetY: headerH + 10, autoKill: true },
                onComplete: function () { history.replaceState(null, "", "#" + id); }
            });
        });
    }

    // ---------------------------------------------------------------- cards: tilt + pointer glow

    function cardInteractions() {
        if (!canHover) return;
        $$(".site-main .card").forEach(function (card) {
            var rx = g.quickTo(card, "rotationX", { duration: 0.45, ease: "power3" });
            var ry = g.quickTo(card, "rotationY", { duration: 0.45, ease: "power3" });
            var lift = g.quickTo(card, "y", { duration: 0.45, ease: "power3" });

            card.addEventListener("pointermove", function (e) {
                var r = card.getBoundingClientRect();
                var px = (e.clientX - r.left) / r.width;
                var py = (e.clientY - r.top) / r.height;
                rx((0.5 - py) * 7);
                ry((px - 0.5) * 9);
                lift(-6);
                card.style.setProperty("--mx", (px * 100).toFixed(1) + "%");
                card.style.setProperty("--my", (py * 100).toFixed(1) + "%");
            });
            card.addEventListener("pointerleave", function () { rx(0); ry(0); lift(0); });
        });
    }

    // ---------------------------------------------------------------- buttons that lean towards the cursor

    function magneticButtons() {
        if (!canHover) return;
        $$(".hero .cta .btn, .download .card .btn").forEach(function (btn) {
            var x = g.quickTo(btn, "x", { duration: 0.4, ease: "power3" });
            var y = g.quickTo(btn, "y", { duration: 0.4, ease: "power3" });
            btn.addEventListener("pointermove", function (e) {
                var r = btn.getBoundingClientRect();
                x((e.clientX - (r.left + r.width / 2)) * 0.25);
                y((e.clientY - (r.top + r.height / 2)) * 0.35);
            });
            btn.addEventListener("pointerleave", function () { x(0); y(0); });
        });
    }

    // ---------------------------------------------------------------- the emulator "switching on"

    function emulatorArrival() {
        var screen = document.getElementById("emu-screen");
        if (!screen) return;
        // Only the shadow is animated - a transform here would break the fullscreen layout.
        ScrollTrigger.create({
            trigger: screen,
            start: "top 80%",
            once: true,
            onEnter: function () {
                g.fromTo(screen,
                    { boxShadow: "0 0 0 0 rgba(143,131,214,0), 0 10px 40px rgba(0,0,0,0.5)" },
                    {
                        boxShadow: "0 0 34px 6px rgba(143,131,214,0.45), 0 10px 40px rgba(0,0,0,0.5)",
                        duration: 0.7, yoyo: true, repeat: 1, ease: "power2.inOut", clearProps: "boxShadow"
                    });
            }
        });
    }

    // ---------------------------------------------------------------- background: raster bars + starfield

    function background() {
        var canvas = document.createElement("canvas");
        canvas.id = "fx-bg";
        canvas.setAttribute("aria-hidden", "true");
        document.body.insertBefore(canvas, document.body.firstChild);
        var ctx = canvas.getContext("2d");
        if (!ctx) return;

        var w = 0, h = 0, stars = [], t = 0, emuRunning = false, ticking = false;

        function resize() {
            var dpr = Math.min(window.devicePixelRatio || 1, 1.5);
            w = vw();
            h = vh();
            canvas.width = Math.round(w * dpr);
            canvas.height = Math.round(h * dpr);
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            stars.length = 0;
            var n = Math.round(Math.min(150, w / 9));
            for (var i = 0; i < n; i++) stars.push({ x: Math.random() * w, y: Math.random() * h, z: Math.random() });
        }
        resize();
        window.addEventListener("resize", debounce(function () { resize(); sync(); }, 200));

        // Detach from the ticker rather than returning early from it: GSAP keeps requestAnimationFrame
        // running for as long as anything is registered, and there is no reason to keep the browser
        // painting once the backdrop has scrolled away - least of all while the emulator has the CPU.
        function wanted() {
            return !document.hidden
                && !emuRunning
                && !document.fullscreenElement
                && window.scrollY < vh() * 1.5;
        }
        function sync() {
            var on = wanted();
            if (on === ticking) return;
            ticking = on;
            if (on) {
                g.ticker.add(render);
            } else {
                g.ticker.remove(render);
                ctx.clearRect(0, 0, w, h);
            }
        }

        // The emulator is by far the most expensive thing on this page, so the backdrop gets out of its
        // way completely: while it runs (i.e. while its overlay is gone) nothing here draws at all.
        var emuRoot = document.getElementById("emu-root");
        if (emuRoot) {
            var check = function () {
                emuRunning = !emuRoot.querySelector(".emu-overlay");
                sync();
            };
            check();
            new MutationObserver(check).observe(emuRoot, { childList: true, subtree: true });
        }
        document.addEventListener("visibilitychange", sync);
        document.addEventListener("fullscreenchange", sync);
        ScrollTrigger.create({ start: 0, end: function () { return vh() * 1.5; }, onToggle: sync });
        sync();

        function render(time, delta) {
            // Fade out over the first screen and a half of scrolling; below that sync() stops the ticker.
            var alpha = 1 - window.scrollY / (vh() * 1.5);
            if (alpha <= 0) { ctx.clearRect(0, 0, w, h); return; }   // clear, in case this is the last frame
            if (alpha > 1) alpha = 1;

            var dt = Math.min(delta, 50) / 16.67;
            t += dt;
            var sy = window.scrollY * 0.12;
            ctx.clearRect(0, 0, w, h);

            // Raster bars: wide soft bands sliding up and down, the way a C64 demo would do it.
            var bh = Math.max(70, h * 0.11);
            for (var i = 0; i < PALETTE.length; i++) {
                var y = h * 0.5 + Math.sin(t * 0.006 + i * 1.05) * h * 0.46 - sy * 0.5;
                var grd = ctx.createLinearGradient(0, y - bh, 0, y + bh);
                grd.addColorStop(0, "rgba(0,0,0,0)");
                grd.addColorStop(0.5, PALETTE[i]);
                grd.addColorStop(1, "rgba(0,0,0,0)");
                ctx.globalAlpha = 0.085 * alpha;
                ctx.fillStyle = grd;
                ctx.fillRect(0, y - bh, w, bh * 2);
            }

            // Starfield scrolling sideways, with a little parallax against the page scroll.
            for (var j = 0; j < stars.length; j++) {
                var s = stars[j];
                s.x -= (0.25 + s.z * 1.35) * dt;
                if (s.x < -2) { s.x = w + 2; s.y = Math.random() * h; }
                var yy = ((s.y - sy * (0.25 + s.z * 0.65)) % h + h) % h;
                var size = s.z > 0.8 ? 2 : 1;
                ctx.globalAlpha = (0.25 + s.z * 0.6) * alpha;
                ctx.fillStyle = s.z > 0.8 ? "#dfe5f0" : "#7d8b9e";
                ctx.fillRect(s.x, yy, size, size);
            }
            ctx.globalAlpha = 1;
        }
    }
})();
