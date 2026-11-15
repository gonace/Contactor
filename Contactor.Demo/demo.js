// The demo page's only script. A click or a key anywhere, in the page or in either frame (both are this site's own),
// keeps the visitor's copy: it tells the demo at most every data-report seconds. After data-idle seconds without one,
// a popup counts down data-warning seconds; with no click or key by then, it asks the demo to end the copy. The demo
// keeps it when the visitor used it since in another tab: then the popup goes and the count starts over. Otherwise the
// copy ends and the page says so. The demo itself deletes a copy whose pages stopped reporting, so this script only
// shows what the demo does anyway.
(() => {
    "use strict";
    const body = document.body;
    const idle = Number(body.dataset.idle) * 1000;
    const warning = Number(body.dataset.warning) * 1000;
    const reportEvery = Number(body.dataset.report) * 1000;
    const popup = document.getElementById("demo-idle");
    const left = document.getElementById("demo-idle-left");
    const ended = document.getElementById("demo-ended");
    let lastActive = Date.now();
    let lastReport = Date.now();
    let reportTimer = null;
    let timingOut = false;
    let over = false;

    // Where the visitor was when the popup opened, to go back there when it goes: an element of the page or of a
    // frame, and the frame the visitor last used, when that element is gone (the frame went to another page).
    let lastFrame = null;
    let focusFrame = null;
    let focusElement = null;

    function report() {
        reportTimer = null;
        lastReport = Date.now();
        fetch(body.dataset.activity, { method: "POST", credentials: "same-origin" })
            .then((response) => { if (response.status === 404) { end(); } })
            .catch(() => { /* Offline for a moment: the next click tries again. */ });
    }

    function active() {
        if (over) {
            return;
        }

        lastActive = Date.now();
        hidePopup();
        if (reportTimer === null) {
            reportTimer = setTimeout(report, Math.max(0, lastReport + reportEvery - Date.now()));
        }
    }

    function showPopup() {
        focusFrame = null;
        focusElement = document.activeElement;
        if (focusElement instanceof HTMLIFrameElement) {
            focusFrame = focusElement;
            try {
                focusElement = focusFrame.contentDocument.activeElement;
            } catch {
                focusElement = null;
            }
        }

        popup.hidden = false;
        document.getElementById("demo-idle-keep").focus();
    }

    function hidePopup() {
        if (popup.hidden) {
            return;
        }

        popup.hidden = true;
        const frame = focusFrame;
        const element = focusElement;
        focusFrame = null;
        focusElement = null;

        // After the click or key that hid it: a click on the popup's backdrop would take the focus away again.
        setTimeout(() => {
            if (over || !popup.hidden) {
                return;
            }

            try {
                const inFrame = frame !== null && frame.isConnected && element !== null
                    && element.ownerDocument === frame.contentDocument;
                const inPage = frame === null && element !== null && element.ownerDocument === document;
                if ((inFrame || inPage) && element.isConnected && element !== element.ownerDocument.body) {
                    if (inFrame) {
                        frame.focus();
                    }

                    element.focus({ preventScroll: true });
                } else if (lastFrame !== null && lastFrame.isConnected) {
                    lastFrame.focus();
                }
            } catch {
                // Nowhere to go back to: the focus stays where it is.
            }
        }, 0);
    }

    // The count down ran out: the demo ends the copy (204), or keeps it for another tab that used it since (409).
    // Anything else (offline, an error on the way) tries again at the next tick.
    function timeOut() {
        timingOut = true;
        fetch(body.dataset.timeout, { method: "POST", credentials: "same-origin" })
            .then((response) => {
                timingOut = false;
                if (response.status === 409) {
                    lastActive = Date.now();
                    hidePopup();
                } else if (response.ok || response.status === 404) {
                    end();
                }
            })
            .catch(() => { timingOut = false; });
    }

    function end() {
        if (over) {
            return;
        }

        over = true;
        clearTimeout(reportTimer);
        popup.hidden = true;
        for (const part of document.querySelectorAll(".demo-header, .demo-frames")) {
            part.remove();
        }

        ended.hidden = false;
        document.getElementById("demo-ended-start").focus();
    }

    function tick() {
        if (over) {
            return;
        }

        const quiet = Date.now() - lastActive;
        if (quiet >= idle) {
            const seconds = Math.max(0, Math.ceil((idle + warning - quiet) / 1000));
            left.textContent = Math.floor(seconds / 60) + ":" + String(seconds % 60).padStart(2, "0");
            if (popup.hidden) {
                showPopup();
            }
        }

        if (quiet >= idle + warning && !timingOut) {
            timeOut();
        }
    }

    // Again after each load: a frame that navigates or reloads has a new document.
    const heardFrom = new WeakSet();
    function listen(target, frame) {
        if (heardFrom.has(target)) {
            return;
        }

        heardFrom.add(target);
        const heard = () => {
            if (frame !== null) {
                lastFrame = frame;
            }

            active();
        };
        target.addEventListener("pointerdown", heard, true);
        target.addEventListener("keydown", heard, true);
    }

    listen(document, null);
    for (const frame of document.querySelectorAll(".demo-frames iframe")) {
        const attach = () => { try { listen(frame.contentDocument, frame); } catch { /* Not this site's: nothing to hear. */ } };
        frame.addEventListener("load", attach);
        attach();
    }

    // "Skip setup": "Go through the setup myself" hides the offer at once and tells the demo; the template's form goes
    // as it is (the page comes back with the app filled in), saying meanwhile that it is on its way.
    const offer = document.getElementById("demo-template");
    if (offer !== null) {
        const setup = document.getElementById("demo-setup-form");
        setup.addEventListener("submit", (event) => {
            event.preventDefault();
            fetch(setup.action, { method: "POST", credentials: "same-origin" }).catch(() => { /* It offers it again on a reload. */ });
            offer.remove();
            const app = document.querySelector(".frame-app");
            app.inert = false;
            app.focus();
        });
        document.getElementById("demo-template-form").addEventListener("submit", () => {
            document.getElementById("demo-template-text").textContent = "Setting up the workshop. This takes a few seconds.";
            setTimeout(() => { for (const button of offer.querySelectorAll("button")) { button.disabled = true; } }, 0);
        });
    }

    setInterval(tick, 1000);
})();
