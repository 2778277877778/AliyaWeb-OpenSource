// 右下角等待动画。
// 先一次性预加载全部帧，再开始按固定间隔播放：
// 原实现每 24ms 直接换 <img src>，首轮还没进缓存会逐帧请求导致明显闪烁。
(function () {
    'use strict';

    const FRAME_DIR = './system/wait/';
    const TOTAL_FRAMES = 34;  // 帧文件为 dian_0.png ~ dian_34.png，共 35 张
    const FRAME_DELAY = 24;   // 每帧间隔毫秒
    const FALLBACK_START_MS = 3000; // 兜底：即使个别帧加载失败也要开始播放

    const container = document.getElementById('wait_anmation');
    if (!container) {
        return;
    }

    const frameSrc = (index) => `${FRAME_DIR}dian_${index}.png`;

    const wait_img = document.createElement('img');
    wait_img.alt = '等待动画帧';
    wait_img.decoding = 'async';

    let currentFrame = 0;
    let timer = null;
    let started = false;

    function updateFrame() {
        wait_img.src = frameSrc(currentFrame);
    }

    function stop() {
        if (timer !== null) {
            clearInterval(timer);
            timer = null;
        }
    }

    function play() {
        stop();
        timer = setInterval(() => {
            currentFrame = (currentFrame + 1) % (TOTAL_FRAMES + 1);
            updateFrame();
        }, FRAME_DELAY);
    }

    // --- 预加载：全部帧进入缓存后再播放 ---
    const total = TOTAL_FRAMES + 1;
    let settled = 0;

    function onFrameSettled() {
        settled += 1;
        if (!started && settled >= total) {
            started = true;
            play();
        }
    }

    for (let i = 0; i < total; i++) {
        const frame = new Image();
        // onerror 也计入，单帧缺失不应导致动画永久停摆
        frame.onload = onFrameSettled;
        frame.onerror = onFrameSettled;
        frame.src = frameSrc(i);
    }

    // 立即显示第一帧，避免加载期间右下角空着
    updateFrame();
    container.appendChild(wait_img);

    // 兜底启动：慢网络下也不至于一直不动
    setTimeout(() => {
        if (!started) {
            started = true;
            play();
        }
    }, FALLBACK_START_MS);

    // 页面切到后台时停掉定时器，省 CPU
    document.addEventListener('visibilitychange', () => {
        if (document.hidden) {
            stop();
        } else if (started) {
            play();
        }
    });

    // 保持原有的窗口尺寸变化重绘行为
    window.addEventListener('resize', updateFrame);
})();
