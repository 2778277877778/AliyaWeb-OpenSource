// 设置面板：游戏原声的开关与音量，以及当前曲目切换。
//
// 按需求独立成一个「设置」按钮 + 独立面板，主界面其余部分不动。
// 状态存在 window.AliyaAudio 里（music.js 负责持久化到 localStorage），
// 本模块只负责界面与它同步；切换曲目/音量都会即时生效。
(function () {
    'use strict';

    const panel = document.getElementById('settings-panel');
    const toggleBtn = document.getElementById('settings-toggle');
    if (!panel || !toggleBtn) {
        return; // 页面上没有设置组件就什么都不做
    }

    const closeBtn = document.getElementById('settings-close');
    const musicEnabled = document.getElementById('set-music-enabled');
    const volume = document.getElementById('set-volume');
    const volumeNum = document.getElementById('set-volume-num');
    const trackName = document.getElementById('set-track-name');
    const trackPos = document.getElementById('set-track-pos');
    const prevBtn = document.getElementById('set-prev');
    const nextBtn = document.getElementById('set-next');

    let isOpen = false;

    const audio = () => window.AliyaAudio || null;

    function syncFromAudio() {
        const a = audio();
        if (!a || typeof a.getState !== 'function') { return; }
        const s = a.getState();
        const pct = Math.round(s.volume * 100);
        if (musicEnabled) { musicEnabled.checked = s.enabled; }
        if (volume) { volume.value = String(pct); }
        if (volumeNum) { volumeNum.textContent = String(pct); }
        if (trackName) { trackName.textContent = s.title; }
        if (trackPos) { trackPos.textContent = (s.index + 1) + ' / ' + s.total; }
    }

    if (musicEnabled) {
        musicEnabled.addEventListener('change', function () {
            const a = audio();
            if (a && typeof a.setEnabled === 'function') { a.setEnabled(!!musicEnabled.checked); }
            syncFromAudio();
        });
    }

    if (volume) {
        volume.addEventListener('input', function () {
            const pct = Number(volume.value);
            const a = audio();
            if (a && typeof a.setVolume === 'function') { a.setVolume(pct / 100); }
            if (volumeNum) { volumeNum.textContent = String(pct); }
        });
    }

    if (prevBtn) {
        prevBtn.addEventListener('click', function () {
            const a = audio();
            if (a && typeof a.prev === 'function') { a.prev(); }
            syncFromAudio();
        });
    }

    if (nextBtn) {
        nextBtn.addEventListener('click', function () {
            const a = audio();
            if (a && typeof a.next === 'function') { a.next(); }
            syncFromAudio();
        });
    }

    // 换曲时（含自动播完切换）同步曲名
    document.addEventListener('aliya:music', function () {
        if (isOpen) { syncFromAudio(); }
    });

    function open() {
        isOpen = true;
        panel.classList.add('open');
        toggleBtn.setAttribute('aria-expanded', 'true');
        // 与「上下文」面板互斥，避免两个抽屉同时盖在一起
        if (window.AliyaCtx && typeof window.AliyaCtx.isOpen === 'function' && window.AliyaCtx.isOpen()) {
            window.AliyaCtx.close();
        }
        syncFromAudio();
    }

    function close() {
        isOpen = false;
        panel.classList.remove('open');
        toggleBtn.setAttribute('aria-expanded', 'false');
    }

    function toggle() { if (isOpen) { close(); } else { open(); } }

    toggleBtn.addEventListener('click', toggle);
    if (closeBtn) { closeBtn.addEventListener('click', close); }

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && isOpen) { close(); }
    });

    syncFromAudio();

    window.AliyaSettings = {
        open: open,
        close: close,
        toggle: toggle,
        isOpen: function () { return isOpen; },
        sync: syncFromAudio,
    };
})();
