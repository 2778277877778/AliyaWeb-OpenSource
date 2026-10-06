// 上下文面板：展示后端已经算好的「长期记忆」（递归压缩总结）与上下文状态，并支持导出成文件。
//
// 用法：点击导航栏上的「上下文」按钮展开/收起。
// 面板打开时，每次对话结束会自动刷新（通过 AliyaCtx.onSessionInfo 被 index.html 调用）。
(function () {
    'use strict';

    const panel = document.getElementById('ctx-panel');
    const toggleBtn = document.getElementById('ctx-toggle');
    const bodyEl = document.getElementById('ctx-body');
    const exportBtn = document.getElementById('ctx-export');
    const downloadBtn = document.getElementById('ctx-download');
    const closeBtn = document.getElementById('ctx-close');

    if (!panel || !toggleBtn) {
        return; // 页面上没有这个组件就什么都不做
    }

    let isOpen = false;
    let busy = false;
    let lastExportPath = null;

    // 与 index.html 里的 getOrCreateSessionId 保持一致；外部脚本可能先于内联脚本执行，所以做个兜底
    function currentSessionId() {
        if (typeof window.getOrCreateSessionId === 'function') {
            return window.getOrCreateSessionId();
        }
        let id = localStorage.getItem('chatSessionId');
        if (!id) {
            id = 'session-' + Date.now() + '-' + Math.random().toString(36).slice(2, 11);
            localStorage.setItem('chatSessionId', id);
        }
        return id;
    }

    const esc = (s) => String(s === undefined || s === null ? '' : s)
        .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

    function fmtTime(ms) {
        if (!ms) { return '—'; }
        try { return new Date(ms).toLocaleString('zh-CN'); } catch (e) { return '—'; }
    }

    function renderEmpty(message) {
        bodyEl.innerHTML = `<p class="ctx-hint">${esc(message)}</p>`;
        exportBtn.disabled = true;
        if (downloadBtn) { downloadBtn.disabled = true; }
    }

    const statRows = (list) => list
        .map(([k, v]) => `<div class="ctx-stat"><span>${esc(k)}</span><b>${esc(v)}</b></div>`)
        .join('');

    function render(data) {
        // 工作窗口：会被记忆整理裁剪，但最近几轮始终保留原文
        const keep = data.keepRecentTurns === undefined ? 2 : data.keepRecentTurns;
        const windowRows = statRows([
            ['当前窗口轮次', `${data.turns} / ${data.summaryTriggerTurns}`],
            ['估算 Token', data.totalTokens],
            ['已整理次数', data.compressedBlocks],
            ['距下次记忆整理', data.turnsUntilCompression > 0 ? `还有 ${data.turnsUntilCompression} 轮` : '下一轮触发'],
        ]);

        // 记忆档案：只追加，不受压缩影响
        const archiveRows = statRows([
            ['记忆总条数', data.archivedMessages],
            ['其中你说过', `${data.archivedUserMessages} 条`],
            ['Aliya 说过', `${data.archivedAiMessages} 条`],
            ['记忆起始', fmtTime(data.archiveSince)],
        ]);

        const summaryHtml = data.hasSummary
            ? `<pre class="ctx-summary">${esc(data.summary)}</pre>`
            : `<p class="ctx-hint">还没有生成长期记忆。<br>每累计 ${data.summaryTriggerTurns} 轮整理一次，`
              + `整理时会保留最近 ${keep} 轮原文。<br>继续聊 ${data.turnsUntilCompression} 轮就会生成。</p>`;

        const emptyNote = data.archivedMessages === 0
            ? '<p class="ctx-hint">档案还是空的：发出第一条消息后就会开始记录。</p>'
            : '';

        const exportHtml = lastExportPath
            ? `<p class="ctx-hint">上次导出：<code>${esc(lastExportPath)}</code></p>`
            : '';

        bodyEl.innerHTML = `
            <h4 class="ctx-h4">上下文状态</h4>
            <div class="ctx-stats">${windowRows}</div>
            <h4 class="ctx-h4">记忆档案（不受压缩影响）</h4>
            <div class="ctx-stats">${archiveRows}</div>
            ${emptyNote}
            <h4 class="ctx-h4">长期记忆（按主题整理）</h4>
            ${summaryHtml}
            ${exportHtml}
        `;

        exportBtn.disabled = false;
        if (downloadBtn) { downloadBtn.disabled = false; }
    }

    async function refresh() {
        if (!isOpen || busy) { return; }
        busy = true;
        bodyEl.innerHTML = '<p class="ctx-hint">读取中…</p>';

        try {
            const res = await fetch(`/api/session/${encodeURIComponent(currentSessionId())}`);
            if (res.status === 404) {
                renderEmpty('还没有对话记录 —— 发出第一条消息后才会建立会话。');
                return;
            }
            if (!res.ok) {
                throw new Error('HTTP ' + res.status);
            }
            render(await res.json());
        } catch (error) {
            bodyEl.innerHTML = `<p class="ctx-hint ctx-error">读取失败：${esc(error.message)}</p>`;
        } finally {
            busy = false;
        }
    }

    async function doExport() {
        const id = currentSessionId();
        exportBtn.disabled = true;
        const old = exportBtn.textContent;
        exportBtn.textContent = '导出中…';

        try {
            const res = await fetch(`/api/session/${encodeURIComponent(id)}/export`, { method: 'POST' });
            const data = await res.json().catch(() => ({}));

            if (!res.ok) {
                throw new Error(data.error || ('HTTP ' + res.status));
            }

            lastExportPath = data.file || data.fileName;
            exportBtn.textContent = `已导出 ${data.bytes} 字节`;
            await refresh();
        } catch (error) {
            exportBtn.textContent = '导出失败';
            bodyEl.insertAdjacentHTML('beforeend',
                `<p class="ctx-hint ctx-error">导出失败：${esc(error.message)}</p>`);
        } finally {
            exportBtn.disabled = false;
            setTimeout(() => { exportBtn.textContent = old; }, 2500);
        }
    }

    // 让浏览器直接下载完整记忆档案（服务端会带 Content-Disposition: attachment）
    function doDownload() {
        const url = `/api/session/${encodeURIComponent(currentSessionId())}/export.md`;
        const a = document.createElement('a');
        a.href = url;
        a.download = '';
        a.rel = 'noopener';
        if (document.body && typeof document.body.appendChild === 'function') {
            document.body.appendChild(a);
            a.click();
            if (typeof a.remove === 'function') { a.remove(); }
        } else {
            window.location.href = url;
        }
    }

    function open() {
        isOpen = true;
        panel.classList.add('open');
        toggleBtn.setAttribute('aria-expanded', 'true');
        // 与「设置」抽屉互斥，避免两个面板叠在一起
        if (window.AliyaSettings && typeof window.AliyaSettings.isOpen === 'function'
            && window.AliyaSettings.isOpen()) {
            window.AliyaSettings.close();
        }
        refresh();
    }

    function close() {
        isOpen = false;
        panel.classList.remove('open');
        toggleBtn.setAttribute('aria-expanded', 'false');
    }

    function toggle() { if (isOpen) { close(); } else { open(); } }

    toggleBtn.addEventListener('click', toggle);
    if (closeBtn) { closeBtn.addEventListener('click', close); }
    if (exportBtn) { exportBtn.addEventListener('click', doExport); }
    if (downloadBtn) { downloadBtn.addEventListener('click', doDownload); }

    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && isOpen) { close(); }
    });

    window.AliyaCtx = {
        open: open,
        close: close,
        toggle: toggle,
        refresh: refresh,
        isOpen: () => isOpen,
        // 由 index.html 在 updateSessionInfo 里调用：面板开着就顺手刷新
        onSessionInfo() { if (isOpen) { refresh(); } },
    };
})();
