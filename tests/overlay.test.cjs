const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function loadOverlay(viewerButtons = {}) {
    const created = [];
    const hostHandlers = {};
    const page = {
        window: {
            chrome: { webview: { addEventListener(name, handler) { hostHandlers[name] = handler; } } },
            addEventListener() {},
        },
        document: {
            readyState: 'loading',
            addEventListener() {},
            getElementById(id) { return viewerButtons[id]; },
            createElement(tag) {
                const element = {
                    tag,
                    style: { cssText: '' },
                    handlers: {},
                    addEventListener(name, handler) { this.handlers[name] = handler; },
                    setAttribute(name, value) { this[name] = value; },
                    getContext() { return {}; },
                };
                created.push(element);
                return element;
            },
        },
    };

    const script = fs.readFileSync(path.join(__dirname, '..', 'src', 'AIM8.App', 'Web', 'overlay.js'), 'utf8');
    vm.runInNewContext(script, page);
    return { created, hostHandlers };
}

test('the live-view drawing canvas does not cover the phone image', () => {
    const { created } = loadOverlay();
    const overlay = created.find(element => element.id === 'aim8-overlay');
    assert.ok(overlay, 'AIM8 should create a separate drawing canvas');
    assert.match(overlay.style.cssText, /(?:^|;)background:transparent(?:;|$)/);
});

test('rotation uses the live viewer controls in both screen and settings modes', () => {
    let left = 0;
    let right = 0;
    const { created, hostHandlers } = loadOverlay({
        'rotate-left': { click() { left++; } },
        'rotate-right': { click() { right++; } },
    });

    hostHandlers.message({ data: { type: 'rotate', direction: 'left' } });
    hostHandlers.message({ data: { type: 'rotate', direction: 'right' } });
    created.find(element => element['aria-label'] === 'Rotate phone left')
        .handlers.click({ stopPropagation() {} });
    created.find(element => element['aria-label'] === 'Rotate phone right')
        .handlers.click({ stopPropagation() {} });

    assert.equal(left, 2);
    assert.equal(right, 2);
});
