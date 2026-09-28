# GSAP (vendored)

GSAP 3.15.0, downloaded from cdnjs and committed here so the site has no CDN dependency
(GitHub Pages serves everything from the same origin, and the page works offline once cached).

    https://cdnjs.cloudflare.com/ajax/libs/gsap/3.15.0/<file>

Files: `gsap.min.js` (core, includes CSSPlugin) plus the `ScrollTrigger`, `ScrollToPlugin`,
`TextPlugin` and `SplitText` plugins. They are plain UMD scripts, loaded with `<script defer>` from
`wwwroot/index.html` (in that order) and used by `wwwroot/js/effects.js`.

GSAP is © GreenSock, used under the standard "no charge" license: https://gsap.com/standard-license
It is not part of the emulator - only of this site's presentation - and is not covered by the
project's MIT license.

To update: change the version in the URL above, re-download the five files, bump the version in the
`<script>` tags' comment in `index.html`, and check the site still animates.
