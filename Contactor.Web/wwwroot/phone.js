// What makes the app feel like an app on a phone. See CLAUDE.md, User interface.

// No pinch zoom on iPhone and iPad. Every browser there uses Safari's engine, which ignores user-scalable=no in the
// viewport (Android's Chrome follows it), so the two-finger gesture itself is cancelled. One-finger scrolling is
// untouched, and iOS's own zoom (Settings → Accessibility → Zoom) still works.
document.addEventListener("gesturestart", (e) => e.preventDefault());
document.addEventListener("touchmove", (e) => {
    if (e.touches.length > 1) {
        e.preventDefault();
    }
}, { passive: false });

// A short buzz when a button marked data-vibrate is tapped (ON/OFF, All off), so you feel the tap went through.
// Android only: iOS gives web pages no vibration. A disabled button gets no click, so it doesn't buzz either.
document.addEventListener("click", (e) => {
    if (e.target instanceof Element && e.target.closest("[data-vibrate]") && "vibrate" in navigator) {
        navigator.vibrate(15);
    }
});
