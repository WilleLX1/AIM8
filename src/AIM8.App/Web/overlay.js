// AIM8 overlay. Injected into every page the mirror WebView loads.
//
// It does two jobs, both on the canvas the phone is drawn into (#c - the
// pymobiledevice3 viewer's canvas, or the one in offline.html):
//
//  1. Capture. A copy of the canvas is written into a shared buffer the host
//     posted to us, and the host is told a frame is ready. One frame is in
//     flight at a time: the next capture waits for the host's answer.
//  2. Draw. The host's analysis (balls, table, shots) is painted on a canvas
//     laid over the phone image. It ignores the mouse, so clicks still reach
//     the phone - except while calibrating, when a drag sets the table corners.
(() => {
    'use strict';
    if (window.__aim8 || !window.chrome || !window.chrome.webview) return;

    const host = window.chrome.webview;
    const S = window.__aim8 = {
        buffer: null,
        bytes: 0,
        waitingBuffer: false,
        waitingSince: 0,
        busy: false,
        busySince: 0,
        seq: 0,
        last: 0,
        interval: 120,
        maxSide: 1800,
        enabled: true,
        labels: true,
        result: null,
        calibrating: false,
        drag: null,
        dpr: 1,
    };

    const TEAM = {
        solids: { color: '#ffd23f', name: 'WHOLE BALLS  1–7' },
        stripes: { color: '#4cc9f0', name: 'HALF BALLS  9–15' },
        open: { color: '#b18cff', name: 'OPEN TABLE' },
    };

    // ---- Host messages ------------------------------------------------------

    host.addEventListener('sharedbufferreceived', (e) => {
        if (S.buffer) {
            try { host.releaseBuffer(S.buffer); } catch (_) { /* already gone */ }
        }
        S.buffer = e.getBuffer();
        S.bytes = S.buffer.byteLength;
        S.waitingBuffer = false;
        S.busy = false;
    });

    host.addEventListener('message', (e) => {
        const m = e.data;
        if (!m || typeof m !== 'object') return;
        switch (m.type) {
            case 'result':
                S.result = m;
                S.busy = false;
                break;
            case 'skip':
                S.busy = false;
                break;
            case 'config':
                S.interval = m.interval;
                S.maxSide = m.maxSide;
                S.enabled = m.enabled;
                S.labels = m.labels;
                if (!S.enabled) S.result = null;
                break;
            case 'calibrate':
                setCalibrating(!!m.on);
                break;
        }
    });

    // ---- Capture ------------------------------------------------------------

    const work = document.createElement('canvas');
    const workCtx = work.getContext('2d', { willReadFrequently: true });

    function capture(now, source) {
        if (!S.enabled || S.busy || S.waitingBuffer) return;
        if (now - S.last < S.interval) return;
        // 300x150 is an untouched canvas: the viewer has not drawn a frame yet.
        if (!source.width || !source.height || (source.width === 300 && source.height === 150)) return;

        const scale = Math.min(1, S.maxSide / Math.max(source.width, source.height));
        const w = Math.max(1, Math.round(source.width * scale));
        const h = Math.max(1, Math.round(source.height * scale));
        const bytes = w * h * 4;

        if (!S.buffer || S.bytes < bytes) {
            S.waitingBuffer = true;
            S.waitingSince = now;
            host.postMessage({ type: 'needBuffer', bytes });
            return;
        }

        if (work.width !== w) work.width = w;
        if (work.height !== h) work.height = h;

        let data;
        try {
            workCtx.drawImage(source, 0, 0, w, h);
            data = workCtx.getImageData(0, 0, w, h).data;
        } catch (err) {
            S.last = now + 3000;
            host.postMessage({ type: 'log', message: 'frame capture failed: ' + err });
            return;
        }

        new Uint8Array(S.buffer, 0, bytes).set(data);
        S.last = now;
        S.busy = true;
        S.busySince = now;
        S.seq++;
        host.postMessage({ type: 'frame', seq: S.seq, w, h });
    }

    // ---- Overlay canvas -----------------------------------------------------

    const overlay = document.createElement('canvas');
    overlay.id = 'aim8-overlay';
    overlay.style.cssText =
        'position:fixed;left:0;top:0;width:0;height:0;pointer-events:none;z-index:2147483600;touch-action:none;';

    function layout(source) {
        if (!overlay.isConnected && document.body) document.body.appendChild(overlay);

        const rect = source.getBoundingClientRect();
        const dpr = window.devicePixelRatio || 1;
        overlay.style.left = rect.left + 'px';
        overlay.style.top = rect.top + 'px';
        overlay.style.width = rect.width + 'px';
        overlay.style.height = rect.height + 'px';

        const bw = Math.max(1, Math.round(rect.width * dpr));
        const bh = Math.max(1, Math.round(rect.height * dpr));
        if (overlay.width !== bw) overlay.width = bw;
        if (overlay.height !== bh) overlay.height = bh;
        S.dpr = dpr;
    }

    function draw() {
        const ctx = overlay.getContext('2d');
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, overlay.width, overlay.height);

        const R = S.result;
        if (R && R.w && S.enabled) {
            const sx = overlay.width / R.w;
            const sy = overlay.height / R.h;
            ctx.setTransform(sx, 0, 0, sy, 0, 0);

            // One CSS pixel, in frame units, so strokes look the same at any size.
            const px = S.dpr / sx;
            drawScene(ctx, R, px);
            ctx.setTransform(S.dpr, 0, 0, S.dpr, 0, 0);
            drawHud(ctx, R);
        }

        if (S.calibrating) drawCalibration(ctx);
    }

    function drawScene(ctx, R, px) {
        const team = TEAM[R.team] || TEAM.solids;
        const moving = R.status !== 'ready';
        const r = R.r || 10;

        if (R.table) {
            const t = R.table;
            ctx.save();
            ctx.strokeStyle = t.manual ? 'rgba(76,154,255,0.7)' : 'rgba(255,255,255,0.22)';
            ctx.lineWidth = px;
            ctx.setLineDash([6 * px, 5 * px]);
            ctx.strokeRect(t.x, t.y, t.w, t.h);
            ctx.restore();

            for (const p of t.pockets) circle(ctx, p[0], p[1], r * 0.45, 'rgba(255,255,255,0.28)', px);
        }

        // Runner-up shots first, faint, so the best one draws over them.
        const shots = moving ? [] : R.shots || [];
        for (let i = shots.length - 1; i >= 1; i--) drawShot(ctx, R, shots[i], px, team, false);

        for (const b of R.balls || []) drawBall(ctx, b, r, px, team, moving);

        if (shots.length) drawShot(ctx, R, shots[0], px, team, true);
    }

    function drawBall(ctx, b, r, px, team, moving) {
        ctx.save();
        ctx.globalAlpha = moving ? 0.55 : 1;

        let color = b.color;
        if (b.kind === 'cue') color = '#ffffff';
        if (b.kind === 'eight') color = '#1d1d1d';
        if (b.kind === 'unknown') color = '#999999';

        const ring = r + 2.5 * px;
        const width = (b.target ? 3 : 1.6) * px;

        if (b.kind === 'eight') {
            // Black on a dark table needs a light rim to show.
            circle(ctx, b.x, b.y, ring + width, 'rgba(255,255,255,0.85)', px);
        }

        ctx.strokeStyle = color;
        ctx.lineWidth = width;
        if (b.kind === 'stripe') ctx.setLineDash([5 * px, 3.5 * px]);
        ctx.beginPath();
        ctx.arc(b.x, b.y, ring, 0, Math.PI * 2);
        ctx.stroke();
        ctx.setLineDash([]);

        if (b.target) {
            ctx.strokeStyle = team.color;
            ctx.lineWidth = 1.2 * px;
            ctx.beginPath();
            ctx.arc(b.x, b.y, ring + 3.5 * px, 0, Math.PI * 2);
            ctx.stroke();
        }

        if (S.labels) {
            const text = b.kind === 'cue' ? '' : b.label;
            if (text) {
                ctx.font = `600 ${11 * px}px "Segoe UI", sans-serif`;
                ctx.textAlign = 'left';
                ctx.textBaseline = 'bottom';
                const x = b.x + r * 0.75;
                const y = b.y - r * 0.75;
                ctx.lineWidth = 3 * px;
                ctx.strokeStyle = 'rgba(0,0,0,0.85)';
                ctx.strokeText(text, x, y);
                ctx.fillStyle = b.kind === 'eight' ? '#ffffff' : color;
                ctx.fillText(text, x, y);
            }
        }

        ctx.restore();
    }

    function drawShot(ctx, R, s, px, team, best) {
        const r = R.r || 10;
        ctx.save();
        ctx.lineCap = 'round';
        ctx.lineJoin = 'round';
        ctx.globalAlpha = best ? 1 : 0.4;
        const main = best ? 3 * px : 1.5 * px;

        if (s.place) {
            // Ball in hand: where to put the cue ball.
            ctx.fillStyle = 'rgba(255,255,255,0.35)';
            ctx.beginPath();
            ctx.arc(s.place[0], s.place[1], r, 0, Math.PI * 2);
            ctx.fill();
            circle(ctx, s.place[0], s.place[1], r, '#ffffff', 1.5 * px);
        }

        // Object ball (and the first ball of a combination) towards the pocket.
        if (s.first && s.first.length > 1) polyline(ctx, s.first, team.color, main * 0.8, [7 * px, 5 * px]);
        if (s.obj && s.obj.length > 1) {
            polyline(ctx, s.obj, team.color, main, s.kind === 'safety' ? [4 * px, 6 * px] : null);
            if (s.kind !== 'safety') arrowHead(ctx, s.obj[s.obj.length - 2], s.obj[s.obj.length - 1], team.color, main, px);
        }

        // Cue ball to the contact point, through a cushion for a kick.
        if (s.cue && s.cue.length > 1) {
            if (best) polyline(ctx, s.cue, 'rgba(0,0,0,0.55)', main + 2.5 * px, null);
            polyline(ctx, s.cue, '#ffffff', main, null);
        }

        // Ghost ball: where the cue ball must be at contact.
        ctx.setLineDash([4 * px, 3 * px]);
        circle(ctx, s.ghost[0], s.ghost[1], r, '#ffffff', (best ? 2 : 1.2) * px);
        ctx.setLineDash([]);

        if (best) {
            // Where the cue ball goes after contact, played without spin.
            if (s.after && s.after.length > 1) {
                polyline(ctx, s.after, s.scratch ? 'rgba(255,90,90,0.85)' : 'rgba(255,255,255,0.55)', 1.5 * px, [2 * px, 5 * px]);
            }

            if (s.pocket >= 0 && R.table) {
                const p = R.table.pockets[s.pocket];
                ctx.save();
                ctx.shadowColor = team.color;
                ctx.shadowBlur = 12 * px;
                circle(ctx, p[0], p[1], r * 1.2, team.color, 2.5 * px);
                ctx.restore();
            }

            if (s.kind !== 'break' && s.kind !== 'safety') {
                const label = Math.round(s.p * 100) + '%';
                ctx.font = `700 ${12 * px}px "Segoe UI", sans-serif`;
                ctx.textAlign = 'center';
                ctx.textBaseline = 'top';
                const x = s.ghost[0];
                const y = s.ghost[1] + r + 4 * px;
                ctx.lineWidth = 3 * px;
                ctx.strokeStyle = 'rgba(0,0,0,0.85)';
                ctx.strokeText(label, x, y);
                ctx.fillStyle = '#ffffff';
                ctx.fillText(label, x, y);
            }
        }

        ctx.restore();
    }

    function drawHud(ctx, R) {
        const team = TEAM[R.team] || TEAM.solids;
        const best = R.status === 'ready' && R.shots && R.shots.length ? R.shots[0] : null;

        let title = team.name + (R.onEight ? '  ·  ON THE 8' : '');
        let line = R.message || '';
        if (best) {
            line = best.desc;
            if (best.kind !== 'break' && best.kind !== 'safety') {
                line += `  ·  ${Math.round(best.p * 100)}%  ·  cut ${best.cut}°`;
                if (best.scratch) line += '  ·  scratch risk';
            }
        }

        ctx.font = '600 12px "Segoe UI", sans-serif';
        const width = Math.max(ctx.measureText(title).width, ctx.measureText(line).width) + 24;
        const x = 8;
        const y = 8;

        ctx.fillStyle = 'rgba(12,14,18,0.78)';
        roundRect(ctx, x, y, Math.min(width, overlay.width / S.dpr - 16), 46, 8);
        ctx.fill();

        ctx.fillStyle = team.color;
        ctx.fillRect(x, y + 8, 3, 30);

        ctx.textAlign = 'left';
        ctx.textBaseline = 'top';
        ctx.fillStyle = team.color;
        ctx.font = '700 11px "Segoe UI", sans-serif';
        ctx.fillText(title, x + 12, y + 8);
        ctx.fillStyle = R.status === 'ready' ? '#e6e9ef' : '#8c94a3';
        ctx.font = '600 12px "Segoe UI", sans-serif';
        ctx.fillText(line, x + 12, y + 25);
    }

    // ---- Calibration --------------------------------------------------------

    function setCalibrating(on) {
        S.calibrating = on;
        S.drag = null;
        overlay.style.pointerEvents = on ? 'auto' : 'none';
        overlay.style.cursor = on ? 'crosshair' : '';
    }

    function relative(e) {
        const rect = overlay.getBoundingClientRect();
        const clamp = (v) => Math.min(1, Math.max(0, v));
        return { x: clamp((e.clientX - rect.left) / rect.width), y: clamp((e.clientY - rect.top) / rect.height) };
    }

    overlay.addEventListener('pointerdown', (e) => {
        if (!S.calibrating) return;
        const p = relative(e);
        S.drag = { x0: p.x, y0: p.y, x1: p.x, y1: p.y };
        try { overlay.setPointerCapture(e.pointerId); } catch (_) { /* fine */ }
        e.preventDefault();
        e.stopPropagation();
    });

    overlay.addEventListener('pointermove', (e) => {
        if (!S.drag) return;
        const p = relative(e);
        S.drag.x1 = p.x;
        S.drag.y1 = p.y;
    });

    overlay.addEventListener('pointerup', (e) => {
        if (!S.drag) return;
        const d = S.drag;
        setCalibrating(false);
        e.stopPropagation();
        if (Math.abs(d.x1 - d.x0) > 0.05 && Math.abs(d.y1 - d.y0) > 0.05) {
            host.postMessage({ type: 'calibrated', x0: d.x0, y0: d.y0, x1: d.x1, y1: d.y1 });
        } else {
            host.postMessage({ type: 'calibrated', cancelled: true });
        }
    });

    // Capture phase, so Escape does not also go to the phone.
    window.addEventListener('keydown', (e) => {
        if (!S.calibrating || e.key !== 'Escape') return;
        setCalibrating(false);
        host.postMessage({ type: 'calibrated', cancelled: true });
        e.preventDefault();
        e.stopPropagation();
    }, true);

    function drawCalibration(ctx) {
        const w = overlay.width / S.dpr;
        const h = overlay.height / S.dpr;
        ctx.setTransform(S.dpr, 0, 0, S.dpr, 0, 0);
        ctx.fillStyle = 'rgba(0,0,0,0.35)';
        ctx.fillRect(0, 0, w, h);

        if (S.drag) {
            const d = S.drag;
            const x = Math.min(d.x0, d.x1) * w;
            const y = Math.min(d.y0, d.y1) * h;
            const rw = Math.abs(d.x1 - d.x0) * w;
            const rh = Math.abs(d.y1 - d.y0) * h;
            ctx.clearRect(x, y, rw, rh);
            ctx.strokeStyle = '#4c9aff';
            ctx.lineWidth = 2;
            ctx.strokeRect(x, y, rw, rh);
        }

        const lines = ['Drag from one inner cushion corner to the opposite one', '(where the cushions meet the cloth)  ·  Esc cancels'];
        ctx.font = '600 13px "Segoe UI", sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        lines.forEach((text, i) => {
            const y = h / 2 + (i - 0.5) * 20;
            ctx.lineWidth = 4;
            ctx.strokeStyle = 'rgba(0,0,0,0.9)';
            ctx.strokeText(text, w / 2, y);
            ctx.fillStyle = '#ffffff';
            ctx.fillText(text, w / 2, y);
        });
    }

    // ---- Drawing helpers ----------------------------------------------------

    function circle(ctx, x, y, radius, color, width) {
        ctx.strokeStyle = color;
        ctx.lineWidth = width;
        ctx.beginPath();
        ctx.arc(x, y, radius, 0, Math.PI * 2);
        ctx.stroke();
    }

    function polyline(ctx, points, color, width, dash) {
        ctx.strokeStyle = color;
        ctx.lineWidth = width;
        ctx.setLineDash(dash || []);
        ctx.beginPath();
        ctx.moveTo(points[0][0], points[0][1]);
        for (let i = 1; i < points.length; i++) ctx.lineTo(points[i][0], points[i][1]);
        ctx.stroke();
        ctx.setLineDash([]);
    }

    function arrowHead(ctx, from, to, color, width, px) {
        const dx = to[0] - from[0];
        const dy = to[1] - from[1];
        const length = Math.hypot(dx, dy);
        if (length < 1e-6) return;
        const ux = dx / length;
        const uy = dy / length;
        const size = 9 * px + width;
        ctx.fillStyle = color;
        ctx.beginPath();
        ctx.moveTo(to[0], to[1]);
        ctx.lineTo(to[0] - ux * size - uy * size * 0.55, to[1] - uy * size + ux * size * 0.55);
        ctx.lineTo(to[0] - ux * size + uy * size * 0.55, to[1] - uy * size - ux * size * 0.55);
        ctx.closePath();
        ctx.fill();
    }

    function roundRect(ctx, x, y, w, h, radius) {
        ctx.beginPath();
        ctx.moveTo(x + radius, y);
        ctx.arcTo(x + w, y, x + w, y + h, radius);
        ctx.arcTo(x + w, y + h, x, y + h, radius);
        ctx.arcTo(x, y + h, x, y, radius);
        ctx.arcTo(x, y, x + w, y, radius);
        ctx.closePath();
    }

    // ---- Main loop ----------------------------------------------------------

    function tick(now) {
        requestAnimationFrame(tick);

        const source = document.getElementById('c');
        if (!source || source === overlay) {
            overlay.style.display = 'none';
            return;
        }

        overlay.style.display = '';
        layout(source);

        // A lost answer must not stall capture for good.
        if (S.busy && now - S.busySince > 4000) S.busy = false;
        if (S.waitingBuffer && now - S.waitingSince > 4000) S.waitingBuffer = false;

        capture(now, source);
        draw();
    }

    function start() {
        host.postMessage({ type: 'hello' });
        requestAnimationFrame(tick);
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();
})();
