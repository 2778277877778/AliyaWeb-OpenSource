// 游戏原声播放器。
//
// 曲目来自 Steam 音乐包 AliyaSoundtrack/MP3 的 7 首（共 33.7MB），按顺序循环播放。
// 关键设计：
//   1. 只创建「一个」<audio>，preload='none'，播完一首才换 src 加载下一首 ——
//      永远只下载当前这一首，不会一次性拉 33.7MB。
//   2. 音量 = 开关 × 音量滑块 × 闪避系数。收音机没对上频道时会持续输出白噪音，
//      这时把音乐压低（闪避），避免两路声音打架；对上频道后自动恢复。
//   3. 开关与音量存在 localStorage，并由设置面板（settings.js）驱动。
//
// 兼容：radio.js 通过 window.AliyaAudio.setDuck() 告知白噪音强度。
(function () {
    'use strict';

    const BASE = './system/music/soundtrack/';
    const TRACKS = [
        { file: '01-aliya.mp3', title: 'Aliya' },
        { file: '02-drift.mp3', title: 'Drift' },
        { file: '03-response.mp3', title: 'Response' },
        { file: '04-letter.mp3', title: 'Letter' },
        { file: '05-astral-sunset.mp3', title: 'Astral Sunset' },
        { file: '06-stars-annihilation.mp3', title: "Stars' Annihilation" },
        { file: '07-tranquil-repose.mp3', title: 'Tranquil Repose' },
    ];

    const STORE_KEY = 'aliya.music';
    const DEFAULT_VOLUME = 0.35;  // 聊天背景音的合适档位
    const DUCK_FLOOR = 0.18;      // 白噪音最响时音乐压到 18%
    const FADE_MS = 600;

    const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

    let audio = null;
    let index = 0;
    let enabled = true;
    let volume = DEFAULT_VOLUME;
    let duckAmount = 0;   // 0 = 无白噪音，1 = 白噪音最响
    let unlocked = false;
    let rampTimer = null;

    // ---------- 持久化 ----------
    (function loadPrefs() {
        try {
            const raw = localStorage.getItem(STORE_KEY);
            if (!raw) { return; }
            const s = JSON.parse(raw);
            if (typeof s.enabled === 'boolean') { enabled = s.enabled; }
            if (typeof s.volume === 'number' && s.volume >= 0 && s.volume <= 1) { volume = s.volume; }
            if (typeof s.index === 'number' && s.index >= 0 && s.index < TRACKS.length) { index = s.index; }
        } catch (e) { /* 存储损坏就用默认值 */ }
    })();

    function savePrefs() {
        try {
            localStorage.setItem(STORE_KEY, JSON.stringify({ enabled: enabled, volume: volume, index: index }));
        } catch (e) { /* 隐私模式等写不了就算了 */ }
    }

    // ---------- 目标音量 ----------
    function targetVolume() {
        if (!enabled) { return 0; }
        const duckFactor = 1 - duckAmount * (1 - DUCK_FLOOR);
        return clamp(volume * duckFactor, 0, 1);
    }

    function rampTo(target) {
        if (!audio) { return; }
        if (rampTimer) { clearInterval(rampTimer); rampTimer = null; }
        const from = audio.volume;
        const delta = target - from;
        if (Math.abs(delta) < 0.005) { audio.volume = target; return; }
        const t0 = performance.now();
        rampTimer = setInterval(() => {
            const k = Math.min(1, (performance.now() - t0) / FADE_MS);
            audio.volume = clamp(from + delta * k, 0, 1);
            if (k >= 1) { clearInterval(rampTimer); rampTimer = null; }
        }, 40);
    }

    function notify() {
        try {
            document.dispatchEvent(new CustomEvent('aliya:music', {
                detail: {
                    enabled: enabled, volume: volume, duck: duckAmount,
                    index: index, title: TRACKS[index].title, total: TRACKS.length,
                    playing: !!(audio && !audio.paused),
                },
            }));
        } catch (e) { /* 老浏览器没有 CustomEvent 就忽略 */ }
    }

    // ---------- 播放控制 ----------
    function apply() {
        if (!audio) { return; }
        const target = targetVolume();

        if (target > 0.005 && unlocked) {
            if (audio.paused) {
                audio.volume = 0;
                const p = audio.play();
                if (p && typeof p.catch === 'function') { p.catch(() => {}); }
            }
            rampTo(target);
        } else {
            rampTo(0);
            if (!audio.paused) {
                setTimeout(() => {
                    if (audio && audio.volume <= 0.005 && !audio.paused) { audio.pause(); }
                }, FADE_MS + 80);
            }
        }
        notify();
    }

    function ensureAudio() {
        if (audio) { return audio; }
        audio = new Audio();
        audio.preload = 'none';   // 只按需加载当前这一首
        audio.loop = false;       // 单曲不循环，靠 ended 事件换下一首
        audio.volume = 0;
        audio.addEventListener('ended', () => { next(true); });
        audio.addEventListener('canplaythrough', () => { apply(); });
        audio.src = BASE + TRACKS[index].file;
        return audio;
    }

    function loadTrack(i, autoplay) {
        index = ((i % TRACKS.length) + TRACKS.length) % TRACKS.length;
        savePrefs();
        if (!audio) { ensureAudio(); }
        audio.src = BASE + TRACKS[index].file;
        try { audio.load(); } catch (e) { /* 忽略 */ }
        if (autoplay !== false) { apply(); } else { notify(); }
    }

    function next(autoplay) { loadTrack(index + 1, autoplay); }
    function prev() { loadTrack(index - 1, true); }

    function unlock() {
        if (!unlocked) {
            unlocked = true;
            ensureAudio();
        }
        // 注意：即使已经解锁也要重新 apply ——
        // 否则「关掉音乐开关再打开」时不会恢复播放（unlock 被提前 return 掉）。
        apply();
    }

    // 用户第一次交互就解锁（浏览器要求交互后才允许播放）
    function firstGesture() {
        document.removeEventListener('pointerdown', firstGesture, true);
        document.removeEventListener('keydown', firstGesture, true);
        unlock();
    }
    document.addEventListener('pointerdown', firstGesture, true);
    document.addEventListener('keydown', firstGesture, true);

    // ---------- 对外接口 ----------
    window.AliyaAudio = {
        unlock: unlock,
        // 收音机用它告知白噪音强度（0..1）：越响，音乐压得越低
        setDuck(amount) {
            duckAmount = clamp(typeof amount === 'number' ? amount : 0, 0, 1);
            if (audio || unlocked) { apply(); }
        },
        setEnabled(on) {
            enabled = !!on;
            savePrefs();
            if (enabled) { unlock(); } else { apply(); }
        },
        setVolume(v) {
            volume = clamp(typeof v === 'number' ? v : DEFAULT_VOLUME, 0, 1);
            savePrefs();
            apply();
        },
        next: next,
        prev: prev,
        getState() {
            return {
                enabled: enabled, volume: volume, duck: duckAmount,
                index: index, title: TRACKS[index].title, total: TRACKS.length,
                unlocked: unlocked,
                playing: !!(audio && !audio.paused),
                effectiveVolume: audio ? audio.volume : 0,
            };
        },
        getTargetVolume: targetVolume,
        tracks: TRACKS.map((t) => t.title),
        srcFor: (i) => BASE + TRACKS[i].file,
    };
})();
