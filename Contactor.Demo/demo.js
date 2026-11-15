// The demo page's only script. A click or a key anywhere, in the page or in either frame (both are this site's own),
// keeps the visitor's copy: it tells the demo at most every data-report seconds. After data-idle seconds without one,
// a popup counts down data-warning seconds; with no click or key by then, the copy ends and the page says so. The demo
// itself deletes a copy whose page stopped reporting, so this script only shows what the demo does anyway.
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
    let over = false;

    function report() {
        reportTimer = null;
        lastReport = Date.now();
        fetch(body.dataset.activity, { method: "POST", credentials: "same-origin" })
            .then((response) => { if (response.status === 404) { end(false); } })
            .catch(() => { /* Offline for a moment: the next click tries again. */ });
    }

    function active() {
        if (over) {
            return;
        }

        lastActive = Date.now();
        if (!popup.hidden) {
            popup.hidden = true;
        }

        if (reportTimer === null) {
            reportTimer = setTimeout(report, Math.max(0, lastReport + reportEvery - Date.now()));
        }
    }

    function end(tellTheDemo) {
        if (over) {
            return;
        }

        over = true;
        clearTimeout(reportTimer);
        if (tellTheDemo) {
            fetch(body.dataset.end, { method: "POST", credentials: "same-origin", keepalive: true }).catch(() => {});
        }

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
        if (quiet >= idle + warning) {
            end(true);
        } else if (quiet >= idle) {
            const seconds = Math.ceil((idle + warning - quiet) / 1000);
            left.textContent = Math.floor(seconds / 60) + ":" + String(seconds % 60).padStart(2, "0");
            if (popup.hidden) {
                popup.hidden = false;
                document.getElementById("demo-idle-keep").focus();
            }
        }
    }

    // Again after each load: a frame that navigates or reloads has a new document.
    function listen(target) {
        target.addEventListener("pointerdown", active, true);
        target.addEventListener("keydown", active, true);
    }

    listen(document);
    for (const frame of document.querySelectorAll(".demo-frames iframe")) {
        const attach = () => { try { listen(frame.contentDocument); } catch { /* Not this site's: nothing to hear. */ } };
        frame.addEventListener("load", attach);
        attach();
    }

    setInterval(tick, 1000);
})();
