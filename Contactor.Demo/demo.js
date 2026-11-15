// The demo page's only script. Before the frames load (their first request starts the visitor's copy), it puts the
// browser's time zone in a short-lived cookie, which the copy runs in; then "Getting your demo ready" covers both
// frames until each has loaded. A click or a key anywhere, in the page or in either frame (both are this site's own),
// keeps the visitor's copy: it tells the demo at most every data-report seconds. After data-idle seconds without one,
// a popup counts down data-warning seconds; with no click or key by then, it asks the demo to end the copy. The demo
// keeps it when the visitor used it since in another tab: then the popup goes and the count starts over. Otherwise the
// copy ends and the page says so; or that it didn't start, or that something went wrong with it, when that is what
// happened. The demo itself deletes a copy
// whose pages stopped reporting, so this script only shows what the demo does anyway.
(() => {
    "use strict";
    const body = document.body;
    const idle = Number(body.dataset.idle) * 1000;
    const warning = Number(body.dataset.warning) * 1000;
    const reportEvery = Number(body.dataset.report) * 1000;
    const popup = document.getElementById("demo-idle");
    const left = document.getElementById("demo-idle-left");
    const ended = document.getElementById("demo-ended");
    const waiting = document.getElementById("demo-waiting");
    const frames = [...document.querySelectorAll(".demo-frames > iframe")];
    const appFrame = document.querySelector(".demo-frames > .frame-app");
    const offer = document.getElementById("demo-template");
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
            .then((response) => { if (response.status === 404) { end("ended"); } })
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
                    end("ended");
                }
            })
            .catch(() => { timingOut = false; });
    }

    // The copy is gone, and the page says how (`how`, as data-demo-copy says it): it "ended", it didn't start
    // ("not-started"), or something went wrong with it after it got going ("went-wrong").
    function end(how) {
        if (over) {
            return;
        }

        over = true;
        if (how === "not-started" || how === "went-wrong") {
            const card = document.getElementById("demo-ended-card");
            const wrong = how === "went-wrong";
            document.getElementById("demo-ended-title").textContent = wrong ? card.dataset.wentWrongTitle : card.dataset.notStartedTitle;
            document.getElementById("demo-ended-text").textContent = wrong ? card.dataset.wentWrongText : card.dataset.notStartedText;
            document.getElementById("demo-ended-start").textContent = card.dataset.notStartedButton;
        }

        clearTimeout(reportTimer);
        clearTimeout(waitTimer);
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

    // The waiting screen: shown until every frame in `loading` has loaded, everything behind it inert meanwhile. When
    // that takes longer than a frame can wait for the copy, the page says so (`how`, as end() takes it), with "Try again".
    let loading = new Set();
    let waitTimer = null;
    function wait(title, text, how) {
        document.getElementById("demo-waiting-title").textContent = title;
        document.getElementById("demo-waiting-text").textContent = text;
        waiting.hidden = false;
        for (const part of [...frames, offer]) {
            if (part !== null) {
                part.inert = true;
            }
        }

        clearTimeout(waitTimer);
        waitTimer = setTimeout(() => end(how), Number(body.dataset.start) * 1000);
    }

    function loaded(frame) {
        if (!loading.delete(frame) || loading.size > 0 || over) {
            return;
        }

        clearTimeout(waitTimer);
        waiting.hidden = true;
        const offered = offer !== null && offer.isConnected;
        for (const each of frames) {
            each.inert = each === appFrame && offered;
        }

        if (offered) {
            offer.inert = false;
        }
    }

    // A frame that shows the demo's own "ended", "didn't start" or "went wrong" (data-demo-copy) has no copy behind it:
    // the whole page says so instead.
    function gone(frame) {
        try {
            const copy = frame.contentDocument.body.dataset.demoCopy;
            if (copy === "ended" || copy === "not-started" || copy === "went-wrong") {
                end(copy);
            }
        } catch {
            // Not this site's, or no document: nothing to read.
        }
    }

    listen(document, null);
    for (const frame of frames) {
        const attach = () => { try { listen(frame.contentDocument, frame); } catch { /* Not this site's: nothing to hear. */ } };
        frame.addEventListener("load", () => { attach(); loaded(frame); gone(frame); });
    }

    try {
        const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
        if (typeof zone === "string" && zone.length > 0 && zone.length <= 64) {
            document.cookie = body.dataset.zoneCookie + "=" + encodeURIComponent(zone) + "; path=/; max-age=600; samesite=strict"
                + (location.protocol === "https:" ? "; secure" : "");
        }
    } catch {
        // No time zone: the copy runs in UTC.
    }

    wait("Getting your demo ready", "One moment: your own copy of the app and a simulated relay board are starting. It takes a few seconds.", "not-started");
    loading = new Set(frames);
    for (const frame of frames) {
        frame.src = frame.dataset.src;
    }

    // "Skip setup": "Go through the setup myself" hides the offer at once and tells the demo. The template is sent by
    // the script too; meanwhile "Setting up the workshop" covers the frames, until the app's frame has loaded again.
    if (offer !== null) {
        const setup = document.getElementById("demo-setup-form");
        setup.addEventListener("submit", (event) => {
            event.preventDefault();
            fetch(setup.action, { method: "POST", credentials: "same-origin" }).catch(() => { /* It offers it again on a reload. */ });
            offer.remove();
            appFrame.inert = false;
            appFrame.focus();
        });
        const template = document.getElementById("demo-template-form");
        template.addEventListener("submit", (event) => {
            event.preventDefault();
            offer.remove();
            // The copy got going already: when the app doesn't come back, something went wrong with it.
            wait("Setting up the workshop", "One moment: the app starts again with the ready-made workshop.", "went-wrong");

            // Only the reload below ends the wait: the app's frame may load something else while its app restarts.
            loading = new Set();
            const reload = () => {
                loading = new Set([appFrame]);
                appFrame.src = appFrame.dataset.src;
            };
            fetch(template.action, { method: "POST", credentials: "same-origin", body: new URLSearchParams(new FormData(template)) })
                .then((response) => {
                    if (response.status === 404) {
                        end("ended");
                    } else if (response.status === 503) {
                        end("not-started");
                    } else if (response.status === 500) {
                        end("went-wrong");
                    } else {
                        reload();
                    }
                })
                .catch(reload);
        });
    }

    setInterval(tick, 1000);
})();
