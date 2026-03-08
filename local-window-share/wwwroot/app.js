const TOUCH_MOVE_THRESHOLD = 10;
const TOUCH_DRAG_HOLD_MS = 260;
const CURSOR_SPEED = 1.18;
const WHEEL_STEP_PIXELS = 20;
const TWO_FINGER_SCROLL_GAIN = 1.8;
const PINCH_DISTANCE_THRESHOLD = 10;
const FRAME_SCALE_MIN = 1;
const FRAME_SCALE_MAX = 4;
const FRAME_RATE_OPTIONS = [15, 30, 45, 60];
const DEFAULT_FRAME_RATE = 30;
const WEBRTC_RECONNECT_COOLDOWN_MS = 180;
const FRAME_RATE_STORAGE_KEY = "windowSharePortal.frameRate";
const STREAM_MODE_OPTIONS = ["low-latency", "balanced", "high-quality"];
const DEFAULT_STREAM_MODE = "balanced";
const STREAM_MODE_STORAGE_KEY = "windowSharePortal.streamMode";
const VIDEO_CODEC_OPTIONS = ["auto", "vp8", "vp9"];
const DEFAULT_VIDEO_CODEC = "auto";
const VIDEO_CODEC_STORAGE_KEY = "windowSharePortal.videoCodec";
const RESIZE_SCALE_STORAGE_KEY = "windowSharePortal.resizeScale";
const LAST_WINDOW_STORAGE_KEY = "windowSharePortal.lastWindow";
const LOOP_SELECTION_STORAGE_KEY = "windowSharePortal.loopSelection";
const TOUCH_MODE_STORAGE_KEY = "windowSharePortal.touchMode";
const CUSTOM_KEYS_STORAGE_KEY = "windowSharePortal.customKeys";
const DIRECT_TAP_HOLD_MS = 400;
const DIRECT_SCROLL_GAIN = 2;
const TITLE_FLICK_THRESHOLD = 40;
const DEFAULT_VIDEO_CODEC_UI_OPTIONS = Object.freeze([
    { value: "auto", label: "Auto", available: true, hint: "15/30fps は VP9、45/60fps と Speed は VP8 を優先します。" },
    { value: "vp8", label: "VP8", available: true, hint: "互換性優先です。" },
    { value: "vp9", label: "VP9", available: true, hint: "高効率です。profile-id=0 を優先して接続します。" },
]);
const FRAME_WIDTH_CAPS_BY_MODE = Object.freeze({
    "low-latency": { 15: 1280, 30: 960, 45: 900, 60: 800 },
    balanced: { 15: 1600, 30: 1280, 45: 1120, 60: 960 },
    "high-quality": { 15: 1920, 30: 1600, 45: 1440, 60: 1280 },
});
const FRAME_DEVICE_PIXEL_RATIO_CAP_BY_MODE = Object.freeze({
    "low-latency": 1.5,
    balanced: 2,
    "high-quality": 2.5,
});
const DOUBLE_TAP_DELAY_MS = 260;
const DOUBLE_TAP_MAX_DISTANCE_PX = 28;

const state = {
    windows: [],
    selectedHandle: null,
    selectedWindow: null,
    selectionGeneration: 0,
    frameTimer: null,
    frameBusy: false,
    frameUrl: null,
    frameVideoWidth: 0,
    frameVideoHeight: 0,
    peerConnection: null,
    signalingSocket: null,
    webrtcGeneration: 0,
    webrtcConnected: false,
    windowsTimer: null,
    serverInfo: null,
    pointerActive: false,
    activePointerId: null,
    activeInputKind: null,
    touchInteractionMode: null,
    pointerLastRatio: null,
    pointerLastClientPoint: null,
    moveRequestInFlight: false,
    queuedMoveRatio: null,
    scrollCarry: 0,
    touchPendingId: null,
    touchPendingPoint: null,
    touchPendingMoved: false,
    touchPendingTimer: null,
    touchTwoFingerScroll: false,
    touchTwoFingerMode: null,
    touchLastCenterPoint: null,
    touchLastDistance: null,
    cursorRatio: null,
    cursorStyle: "mouse",
    frameScale: 1,
    frameAutoScale: 1,
    frameTranslateX: 0,
    frameTranslateY: 0,
    frameRate: loadFrameRatePreference(),
    streamMode: loadStreamModePreference(),
    videoCodecPreference: loadVideoCodecPreference(),
    pendingTapTimer: null,
    pendingTapRatio: null,
    pendingTapTime: 0,
    scrollPadActive: false,
    scrollPadPointerId: null,
    scrollPadLastClientY: null,
    scrollPadCarry: 0,
    drawerOpen: false,
    activeStreamHandle: null,
    activeStreamMaxWidth: null,
    streamTargetSyncTimer: null,
    loopHandles: new Set(),
    viewMode: "window",
    savedWindowBounds: null,
    resizeBusy: false,
    resizeScale: loadResizeScalePreference(),
    imeComposing: false,
    touchMode: loadTouchModePreference(),
    directTouchId: null,
    directTouchStartPoint: null,
    directTouchStartRatio: null,
    directTouchMode: null,
    directTouchHoldTimer: null,
    directTouchTapTime: 0,
    directTouchTapRatio: null,
};

const elements = {
    loginPanel: document.getElementById("login-panel"),
    loginForm: document.getElementById("login-form"),
    loginError: document.getElementById("login-error"),
    logoutButton: document.getElementById("logout-button"),
    workspace: document.getElementById("workspace"),
    menuButton: document.getElementById("menu-button"),
    drawerBackdrop: document.getElementById("drawer-backdrop"),
    sideDrawer: document.getElementById("side-drawer"),
    drawerCloseButton: document.getElementById("drawer-close-button"),
    refreshButton: document.getElementById("refresh-button"),
    launchExplorer: document.getElementById("launch-explorer"),
    launchCmd: document.getElementById("launch-cmd"),
    touchModePointerButton: document.getElementById("touch-mode-pointer"),
    touchModeDirectButton: document.getElementById("touch-mode-direct"),
    modeWindowButton: document.getElementById("mode-window-button"),
    modeMonitorButton: document.getElementById("mode-monitor-button"),
    monitorList: document.getElementById("monitor-list"),
    windowList: document.getElementById("window-list"),
    windowPrevButton: document.getElementById("window-prev-button"),
    windowNextButton: document.getElementById("window-next-button"),
    viewerWindowTitle: document.getElementById("viewer-window-title"),
    selectedTitle: document.getElementById("selected-title"),
    selectedMeta: document.getElementById("selected-meta"),
    activateButton: document.getElementById("activate-button"),
    frameRefreshButton: document.getElementById("frame-refresh-button"),
    frameStage: document.querySelector(".frame-stage"),
    windowFrame: document.getElementById("window-frame"),
    frameCursor: document.getElementById("frame-cursor"),
    framePlaceholder: document.getElementById("frame-placeholder"),
    viewerStatus: document.getElementById("viewer-status"),
    frameRateSelect: document.getElementById("frame-rate-select"),
    streamModeButtons: Array.from(document.querySelectorAll(".stream-mode-button")),
    streamModeHint: document.getElementById("stream-mode-hint"),
    videoCodecButtons: Array.from(document.querySelectorAll(".video-codec-button")),
    videoCodecHint: document.getElementById("video-codec-hint"),
    textForm: document.getElementById("text-form"),
    textInput: document.getElementById("text-input"),
    textSubmit: document.getElementById("text-submit"),
    quickKeyButtons: Array.from(document.querySelectorAll(".quick-key-button")),
    scrollPadLeft: document.getElementById("scroll-pad-left"),
    scrollPad: document.getElementById("scroll-pad"),
    selectAllButton: document.getElementById("select-all-button"),
    selectWtButton: document.getElementById("select-wt-button"),
    selectNoneButton: document.getElementById("select-none-button"),
    resizeScaleSelect: document.getElementById("resize-scale-select"),
    resizeToggleButton: document.getElementById("resize-toggle-button"),
    centerButton: document.getElementById("center-button"),
    infoBar: document.getElementById("info-bar"),
    customKeyList: document.getElementById("custom-key-list"),
    customKeyInput: document.getElementById("custom-key-input"),
    customKeyLabel: document.getElementById("custom-key-label"),
    customKeyAdd: document.getElementById("custom-key-add"),
    keyDropdown: document.getElementById("key-dropdown"),
    modCtrl: document.getElementById("mod-ctrl"),
    modShift: document.getElementById("mod-shift"),
    modAlt: document.getElementById("mod-alt"),
    modWin: document.getElementById("mod-win"),
};

document.addEventListener("DOMContentLoaded", () => {
    registerGlobalClientLogging();
    applyTouchModeUI();
    bindEvents();
    loadCustomKeys();
    bootstrap().catch(showFatalError);
});

function bindEvents() {
    document.addEventListener("gesturestart", preventBrowserZoom, { passive: false });
    document.addEventListener("gesturechange", preventBrowserZoom, { passive: false });
    document.addEventListener("gestureend", preventBrowserZoom, { passive: false });
    document.addEventListener("touchend", preventDoubleTapZoom, { passive: false });
    document.addEventListener("dblclick", (event) => event.preventDefault());
    elements.loginForm.addEventListener("submit", (event) => {
        handleLogin(event).catch(showFatalError);
    });
    elements.logoutButton.addEventListener("click", () => {
        handleLogout().catch(showTransientError);
    });
    elements.menuButton.addEventListener("click", () => toggleDrawer());
    elements.drawerBackdrop.addEventListener("click", () => closeDrawer());
    elements.drawerCloseButton.addEventListener("click", () => closeDrawer());
    document.addEventListener("keydown", handleGlobalKeyDown);

    elements.touchModePointerButton.addEventListener("click", () => setTouchMode("pointer"));
    elements.touchModeDirectButton.addEventListener("click", () => setTouchMode("direct"));
    elements.modeWindowButton.addEventListener("click", () => switchViewMode("window"));
    elements.modeMonitorButton.addEventListener("click", () => switchViewMode("monitor"));

    elements.refreshButton.addEventListener("click", () => {
        refreshWindows(true).catch(showTransientError);
    });
    elements.launchExplorer.addEventListener("click", () => {
        launchApp("explorer").catch(showTransientError);
    });
    elements.launchCmd.addEventListener("click", () => {
        launchApp("cmd").catch(showTransientError);
    });
    elements.windowPrevButton.addEventListener("click", () => {
        selectAdjacentWindow(-1).catch(showTransientError);
    });
    elements.windowNextButton.addEventListener("click", () => {
        selectAdjacentWindow(1).catch(showTransientError);
    });
    elements.activateButton.addEventListener("click", () => {
        handleActivateWindow().catch(showTransientError);
    });
    elements.frameRefreshButton.addEventListener("click", () => {
        refreshFrameNow(true).catch(showTransientError);
    });
    elements.resizeToggleButton.addEventListener("click", () => {
        toggleMobileResize().catch(showTransientError);
    });
    elements.centerButton.addEventListener("click", () => {
        state.frameTranslateX = 0;
        state.frameTranslateY = 0;
        state.frameScale = 1;
        syncFrameTransform();
    });
    elements.textInput.addEventListener("compositionstart", () => { state.imeComposing = true; });
    elements.textInput.addEventListener("compositionend", () => { state.imeComposing = false; });
    elements.textForm.addEventListener("submit", (event) => {
        if (state.imeComposing) { event.preventDefault(); return; }
        handleSendText(event).catch(showTransientError);
    });
    elements.frameRateSelect.addEventListener("change", handleFrameRateChange);
    elements.resizeScaleSelect.addEventListener("change", handleResizeScaleChange);
    for (const button of elements.streamModeButtons) {
        button.addEventListener("click", () => {
            handleStreamModeChange(button.dataset.streamMode).catch(showTransientError);
        });
    }
    for (const button of elements.videoCodecButtons) {
        button.addEventListener("click", () => {
            handleVideoCodecChange(button.dataset.videoCodec).catch(showTransientError);
        });
    }

    // Title bar flick to switch windows
    {
        let startX = 0;
        let startY = 0;
        let active = false;
        elements.infoBar.addEventListener("touchstart", (e) => {
            if (e.touches.length !== 1) return;
            active = true;
            startX = e.touches[0].clientX;
            startY = e.touches[0].clientY;
        }, { passive: true });
        elements.infoBar.addEventListener("touchmove", (e) => {
            if (active) e.preventDefault();
        }, { passive: false });
        elements.infoBar.addEventListener("touchend", (e) => {
            if (!active) return;
            active = false;
            const touch = e.changedTouches[0];
            const dx = touch.clientX - startX;
            const dy = touch.clientY - startY;
            if (Math.abs(dx) > TITLE_FLICK_THRESHOLD && Math.abs(dx) > Math.abs(dy)) {
                selectAdjacentWindow(dx < 0 ? 1 : -1).catch(showTransientError);
            }
        }, { passive: true });
        elements.infoBar.addEventListener("touchcancel", () => { active = false; }, { passive: true });
    }

    elements.customKeyAdd.addEventListener("click", addCustomKey);
    elements.customKeyInput.addEventListener("focus", () => showKeyDropdown(elements.customKeyInput.value));
    elements.customKeyInput.addEventListener("input", () => showKeyDropdown(elements.customKeyInput.value));
    elements.customKeyInput.addEventListener("blur", () => setTimeout(hideKeyDropdown, 150));
    for (const btn of [elements.modCtrl, elements.modShift, elements.modAlt, elements.modWin]) {
        btn.addEventListener("click", () => btn.classList.toggle("active"));
    }

    elements.windowFrame.addEventListener("contextmenu", (event) => event.preventDefault());
    elements.windowFrame.addEventListener("loadedmetadata", handleFrameMetadataLoaded);
    elements.windowFrame.addEventListener("resize", handleFrameMetadataLoaded);
    elements.windowFrame.addEventListener("playing", handleFrameMetadataLoaded);
    elements.windowFrame.addEventListener("pointerdown", (event) => {
        handleFramePointerDown(event).catch(showTransientError);
    });
    elements.windowFrame.addEventListener("pointermove", handleFramePointerMove);
    elements.windowFrame.addEventListener("pointerup", (event) => {
        handleFramePointerUp(event).catch(showTransientError);
    });
    elements.windowFrame.addEventListener("pointercancel", handleFramePointerCancel);
    elements.windowFrame.addEventListener("touchstart", (event) => {
        handleFrameTouchStart(event).catch(showTransientError);
    }, { passive: false });
    elements.windowFrame.addEventListener("touchmove", (event) => {
        handleFrameTouchMove(event).catch(showTransientError);
    }, { passive: false });
    elements.windowFrame.addEventListener("touchend", (event) => {
        handleFrameTouchEnd(event).catch(showTransientError);
    }, { passive: false });
    elements.windowFrame.addEventListener("touchcancel", handleFrameTouchCancel, { passive: false });
    window.addEventListener("resize", () => {
        updateAutoFrameScale();
        constrainFrameTransform();
        syncFrameTransform();
        syncFrameCursor();
        scheduleActiveStreamTargetSync();
    });

    for (const button of elements.quickKeyButtons) {
        const key = button.dataset.key;
        button.addEventListener("click", () => {
            sendKey(key);
        });

        if (key === "Backspace") {
            let repeatTimer = null;
            const stopRepeat = () => {
                if (repeatTimer !== null) {
                    clearInterval(repeatTimer);
                    repeatTimer = null;
                }
            };
            button.addEventListener("pointerdown", (e) => {
                if (e.button !== 0) return;
                stopRepeat();
                repeatTimer = setInterval(() => sendKey(key), 120);
            });
            button.addEventListener("pointerup", stopRepeat);
            button.addEventListener("pointercancel", stopRepeat);
            button.addEventListener("pointerleave", stopRepeat);
        }
    }

    elements.selectAllButton.addEventListener("click", () => {
        for (const w of state.windows) {
            if (w.handle >= 0 && !w.isMinimized) state.loopHandles.add(w.handle);
        }
        applyLoopSelectionChange();
    });

    elements.selectWtButton.addEventListener("click", () => {
        state.loopHandles.clear();
        for (const w of state.windows) {
            if (w.handle >= 0 && !w.isMinimized && /WindowsTerminal|wt\b/i.test(w.processName)) {
                state.loopHandles.add(w.handle);
            }
        }
        applyLoopSelectionChange();
    });

    elements.selectNoneButton.addEventListener("click", () => {
        state.loopHandles.clear();
        applyLoopSelectionChange();
    });

    elements.scrollPad.addEventListener("pointerdown", (event) => {
        handleScrollPadPointerDown(event, elements.scrollPad);
    });
    elements.scrollPad.addEventListener("pointermove", handleScrollPadPointerMove);
    elements.scrollPad.addEventListener("pointerup", handleScrollPadPointerUp);
    elements.scrollPad.addEventListener("pointercancel", handleScrollPadPointerCancel);

    elements.scrollPadLeft.addEventListener("pointerdown", (event) => {
        handleScrollPadPointerDown(event, elements.scrollPadLeft);
    });
    elements.scrollPadLeft.addEventListener("pointermove", handleScrollPadPointerMove);
    elements.scrollPadLeft.addEventListener("pointerup", handleScrollPadPointerUp);
    elements.scrollPadLeft.addEventListener("pointercancel", handleScrollPadPointerCancel);
}

async function bootstrap() {
    const authorized = await ensureAuthorized();
    if (!authorized) {
        showLoggedOutState();
        return;
    }

    await showWorkspace();
}

async function ensureAuthorized() {
    const response = await fetch("/api/server-info", { credentials: "same-origin" });
    if (response.status === 401) {
        return false;
    }

    if (!response.ok) {
        throw new Error("Failed to load server info.");
    }

    state.serverInfo = await response.json();
    return true;
}

async function showWorkspace() {
    elements.loginPanel.hidden = true;
    elements.workspace.hidden = false;
    elements.logoutButton.hidden = false;
    elements.drawerBackdrop.hidden = false;
    renderFrameRateSelection();
    renderStreamModeSelection();
    renderVideoCodecSelection();
    renderResizeScaleSelection();
    renderServerInfo();
    closeDrawer(true);
    await refreshWindows(false);

    if (state.windowsTimer) {
        window.clearInterval(state.windowsTimer);
    }

    state.windowsTimer = window.setInterval(() => {
        refreshWindows(true).catch(showTransientError);
    }, 5000);
}

function showLoggedOutState() {
    if (state.windowsTimer) {
        window.clearInterval(state.windowsTimer);
        state.windowsTimer = null;
    }

    elements.workspace.hidden = true;
    elements.loginPanel.hidden = false;
    elements.logoutButton.hidden = true;
    elements.drawerBackdrop.hidden = true;
    state.serverInfo = null;
    closeDrawer(true);
    resetSelection();
    stopFrameLoop();
    disconnectWebRtcSession({ clearVideo: true });
}

function renderServerInfo() {
}

function renderFrameRateSelection() {
    if (elements.frameRateSelect) {
        elements.frameRateSelect.value = String(state.frameRate);
    }
}

function renderStreamModeSelection() {
    for (const button of elements.streamModeButtons) {
        button.classList.toggle("active", button.dataset.streamMode === state.streamMode);
    }

    if (!elements.streamModeHint) {
        return;
    }

    elements.streamModeHint.textContent = state.streamMode === "high-quality"
        ? "Quality: 高解像度優先。遅延は少し増えます。"
        : state.streamMode === "low-latency"
            ? "Speed: 低遅延優先。画質は少し落とします。"
            : "Balance: 画質と遅延のバランスを取ります。";
}

function renderVideoCodecSelection() {
    const options = getVideoCodecUiOptions();
    const selectedValue = ensureSupportedVideoCodecPreference(state.videoCodecPreference, options);
    const effectiveValue = resolveEffectiveVideoCodecPreference(selectedValue, options);

    for (const button of elements.videoCodecButtons) {
        const value = normalizeVideoCodecPreference(button.dataset.videoCodec);
        const option = options.find((current) => current.value === value);
        const available = option?.available !== false;
        button.classList.toggle("active", value === selectedValue);
        button.disabled = !available;
        if (option?.label) {
            button.textContent = option.label;
        }
        button.title = option?.hint || "";
    }

    if (!elements.videoCodecHint) {
        return;
    }

    const selectedOption = options.find((option) => option.value === selectedValue) || options[0];
    const effectiveSuffix = selectedValue === "auto"
        ? ` 現在は ${videoCodecLabel(effectiveValue)} を使います。`
        : "";
    elements.videoCodecHint.textContent = `${selectedOption.label}: ${selectedOption.hint}${effectiveSuffix}`;
}

async function handleLogin(event) {
    event.preventDefault();
    elements.loginError.hidden = true;

    const token = new FormData(elements.loginForm).get("token");
    const submitButton = elements.loginForm.querySelector("button[type=submit]");
    const tokenInput = elements.loginForm.querySelector("input[name=token]");

    submitButton.disabled = true;
    tokenInput.disabled = true;
    showLoginStatus("サーバーに接続しています...");

    let response;
    try {
        const controller = new AbortController();
        const statusTimer = setTimeout(() => {
            showLoginStatus("管理者の承認を待っています...");
        }, 1500);

        response = await fetch("/api/login", {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ token }),
            signal: controller.signal,
        });

        clearTimeout(statusTimer);
    } catch (err) {
        hideLoginStatus();
        submitButton.disabled = false;
        tokenInput.disabled = false;
        elements.loginError.hidden = false;
        elements.loginError.textContent = "接続に失敗しました。";
        return;
    }

    hideLoginStatus();
    submitButton.disabled = false;
    tokenInput.disabled = false;

    if (!response.ok) {
        elements.loginError.hidden = false;
        try {
            const body = await response.json();
            if (body.reason === "approval_required") {
                elements.loginError.textContent = "管理者に承認されませんでした。もう一度お試しください。";
                return;
            }
        } catch {}
        elements.loginError.textContent = "Token が違います。";
        return;
    }

    elements.loginForm.reset();
    await bootstrap();
}

function showLoginStatus(message) {
    let statusEl = document.getElementById("login-status");
    if (!statusEl) {
        statusEl = document.createElement("p");
        statusEl.id = "login-status";
        statusEl.className = "login-status";
        const errorEl = document.getElementById("login-error");
        errorEl.parentNode.insertBefore(statusEl, errorEl);
    }
    statusEl.textContent = message;
    statusEl.hidden = false;
}

function hideLoginStatus() {
    const statusEl = document.getElementById("login-status");
    if (statusEl) statusEl.hidden = true;
}

async function handleLogout() {
    await fetch("/api/logout", {
        method: "POST",
        credentials: "same-origin",
    });

    showLoggedOutState();
}

async function toggleMobileResize() {
    if (!state.selectedHandle || state.resizeBusy || state.selectedHandle < 0) {
        return;
    }
    state.resizeBusy = true;
    try {
        await _doToggleMobileResize();
    } finally {
        state.resizeBusy = false;
    }
}

async function _doToggleMobileResize() {

    if (state.savedWindowBounds) {
        const bounds = state.savedWindowBounds;
        const response = await fetch(`/api/windows/${state.selectedHandle}/resize`, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ width: bounds.width, height: bounds.height }),
        });

        if (!response.ok) {
            const body = await response.json().catch(() => ({}));
            throw new Error(body.message || "Failed to restore window size.");
        }

        const result = await response.json();
        applySelectedWindowBounds(result.appliedBounds || bounds);
        state.savedWindowBounds = null;
        elements.resizeToggleButton.classList.remove("active");
        elements.viewerStatus.textContent = formatResizeStatus("Restored", result.appliedBounds || bounds, bounds);
    } else {
        const scale = state.resizeScale;
        const stageRect = elements.frameStage.getBoundingClientRect();
        const headerHeight = 44;
        const scrollRailWidth = 32;
        const availableWidth = Math.floor(stageRect.width - scrollRailWidth);
        const availableHeight = Math.floor(stageRect.height - headerHeight);
        const targetWidth = Math.max(Math.floor(300 * scale), Math.min(availableWidth, Math.floor(600 * scale)));
        const targetHeight = Math.max(Math.floor(400 * scale), Math.min(availableHeight, Math.floor(1200 * scale)));

        const response = await fetch(`/api/windows/${state.selectedHandle}/resize`, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ width: targetWidth, height: targetHeight }),
        });

        if (!response.ok) {
            const body = await response.json().catch(() => ({}));
            throw new Error(body.message || "Failed to resize window.");
        }

        const result = await response.json();
        applySelectedWindowBounds(result.appliedBounds || { width: targetWidth, height: targetHeight });
        state.savedWindowBounds = result.previousBounds;
        elements.resizeToggleButton.classList.add("active");
        elements.viewerStatus.textContent = formatResizeStatus("Resized", result.appliedBounds || { width: targetWidth, height: targetHeight }, { width: targetWidth, height: targetHeight });
    }

}

function applySelectedWindowBounds(bounds) {
    if (!bounds || !state.selectedWindow?.bounds) {
        return;
    }

    state.selectedWindow.bounds = {
        ...state.selectedWindow.bounds,
        left: bounds.left,
        top: bounds.top,
        width: bounds.width,
        height: bounds.height,
    };
    updateSelectedWindowPresentation();
}

function formatResizeStatus(action, appliedBounds, requestedBounds) {
    if (!appliedBounds) {
        return `${action}.`;
    }

    const appliedWidth = appliedBounds.width;
    const appliedHeight = appliedBounds.height;
    if (!requestedBounds) {
        return `${action} to ${appliedWidth}x${appliedHeight}.`;
    }

    if (appliedWidth !== requestedBounds.width || appliedHeight !== requestedBounds.height) {
        return `${action} to ${appliedWidth}x${appliedHeight} to fit the monitor.`;
    }

    return `${action} to ${appliedWidth}x${appliedHeight}.`;
}

async function launchApp(app) {
    const response = await fetch("/api/launch", {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ app }),
    });
    if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error(body.message || "Failed to launch app.");
    }
    elements.viewerStatus.textContent = `Launched ${app}.`;
    setTimeout(() => refreshWindows(true).catch(showTransientError), 1500);
}

async function refreshWindows(preserveSelection) {
    const response = await fetch("/api/windows", { credentials: "same-origin" });
    if (response.status === 401) {
        showLoggedOutState();
        return;
    }

    if (!response.ok) {
        throw new Error("Failed to list windows.");
    }

    const payload = await response.json();
    renderWindowList(payload.windows);

    if (preserveSelection && state.selectedHandle) {
        const selectedSummary = payload.windows.find((windowInfo) => windowInfo.handle === state.selectedHandle);
        const stillExists = Boolean(selectedSummary);
        if (stillExists) {
            if (selectedSummary && state.selectedWindow) {
                state.selectedWindow = {
                    ...state.selectedWindow,
                    title: selectedSummary.title,
                    processId: selectedSummary.processId,
                    processName: selectedSummary.processName,
                    className: selectedSummary.className,
                    isMinimized: selectedSummary.isMinimized,
                    isForeground: selectedSummary.isForeground,
                    bounds: selectedSummary.bounds,
                };
                updateSelectedWindowPresentation();
            }
            highlightSelectedWindow();
            scheduleActiveStreamTargetSync();
            return;
        }
    }

    if (payload.windows.length > 0) {
        const lastHandle = loadLastWindowPreference();
        const lastWindow = lastHandle ? payload.windows.find((w) => w.handle === lastHandle) : null;
        await selectWindow(lastWindow ? lastWindow.handle : payload.windows[0].handle, false);
        return;
    }

    resetSelection();
}

function renderWindowList(windows) {
    state.windows = Array.isArray(windows) ? windows.slice() : [];
    const monitors = state.windows.filter((w) => w.handle < 0);
    const windowsOnly = state.windows.filter((w) => w.handle >= 0);

    // 初回は全選択、以降は保存された選択を復元
    if (state.loopHandles.size === 0 && !state.loopInitialized) {
        state.loopInitialized = true;
        const saved = loadLoopSelection();
        if (saved) {
            for (const h of saved) {
                if (windowsOnly.some((w) => w.handle === h)) state.loopHandles.add(h);
            }
        } else {
            for (const w of windowsOnly) state.loopHandles.add(w.handle);
        }
    }

    // ループに含まれていたが消えたハンドルを除去
    const validHandles = new Set(windowsOnly.map((w) => w.handle));
    for (const h of state.loopHandles) {
        if (!validHandles.has(h)) state.loopHandles.delete(h);
    }

    // 新しいウィンドウはデフォルトでループに追加
    for (const w of windowsOnly) {
        if (!state.loopHandles.has(w.handle) && !state.loopInitialized) {
            state.loopHandles.add(w.handle);
        }
    }

    elements.monitorList.innerHTML = "";
    if (monitors.length === 0) {
        elements.monitorList.innerHTML = '<p class="empty-copy">No monitors detected.</p>';
    } else {
        for (const info of monitors) {
            elements.monitorList.appendChild(buildMonitorCard(info));
        }
    }

    elements.windowList.innerHTML = "";
    if (windowsOnly.length === 0) {
        elements.windowList.innerHTML = '<p class="empty-copy">No shareable windows detected.</p>';
    } else {
        for (const info of windowsOnly) {
            elements.windowList.appendChild(buildWindowToggleCard(info));
        }
    }

    highlightSelectedWindow();
    renderHeaderNavigation();
}

function buildMonitorCard(windowInfo) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "window-card";
    button.dataset.handle = String(windowInfo.handle);
    button.addEventListener("click", () => {
        switchViewMode("monitor");
        selectWindow(windowInfo.handle, true).catch(showTransientError);
    });

    button.innerHTML = `
        <div class="window-card-row">
            <strong class="window-card-title">${escapeHtml(windowInfo.title)}</strong>
            <span class="pill pill-screen">screen</span>
        </div>
        <p class="window-card-meta">${windowInfo.bounds.width} x ${windowInfo.bounds.height}</p>
    `;

    return button;
}

function buildWindowToggleCard(windowInfo) {
    const card = document.createElement("div");
    const inLoop = state.loopHandles.has(windowInfo.handle);
    card.className = "window-card" + (inLoop ? " loop-active" : "");
    card.dataset.handle = String(windowInfo.handle);

    const pillClass = windowInfo.isMinimized ? "pill-warn" : "pill-live";
    const pillLabel = windowInfo.isMinimized ? "min" : "live";

    const body = document.createElement("div");
    body.className = "window-card-body";
    body.addEventListener("click", () => {
        toggleWindowLoop(windowInfo.handle);
    });
    body.innerHTML = `
        <div class="window-card-row">
            <strong class="window-card-title">${escapeHtml(windowInfo.title)}</strong>
            <span class="pill ${pillClass}">${pillLabel}</span>
        </div>
        <p class="window-card-meta">${escapeHtml(windowInfo.processName)} · PID ${windowInfo.processId}</p>
        <p class="window-card-meta">${windowInfo.bounds.width} x ${windowInfo.bounds.height}</p>
    `;

    const selectBtn = document.createElement("button");
    selectBtn.type = "button";
    selectBtn.className = "window-card-select";
    selectBtn.textContent = "Select";
    selectBtn.addEventListener("click", (e) => {
        e.stopPropagation();
        (async () => {
            switchViewMode("window");
            if (!state.loopHandles.has(windowInfo.handle)) {
                state.loopHandles.add(windowInfo.handle);
                applyLoopSelectionChange();
            }
            if (windowInfo.isMinimized) {
                await fetch(`/api/windows/${windowInfo.handle}/activate`, {
                    method: "POST",
                    credentials: "same-origin",
                });
            }
            await selectWindow(windowInfo.handle, true);
        })().catch(showTransientError);
    });

    card.appendChild(body);
    card.appendChild(selectBtn);
    return card;
}

function toggleWindowLoop(handle) {
    if (state.loopHandles.has(handle)) {
        state.loopHandles.delete(handle);
        // トグルOFFしたのが表示中なら次のループウィンドウへ移動
        if (state.selectedHandle === handle) {
            const loopWindows = getNavigableWindows();
            if (loopWindows.length > 0) {
                selectWindow(loopWindows[0].handle, false).catch(showTransientError);
            } else {
                resetSelection();
            }
        }
    } else {
        state.loopHandles.add(handle);
        switchViewMode("window");
        // 初めてのトグルON、または未選択なら表示切替
        const loopWindows = getNavigableWindows();
        if (loopWindows.length === 1 || state.selectedHandle == null) {
            selectWindow(handle, false).catch(showTransientError);
        }
    }
    saveLoopSelection();
    refreshWindowToggleCards();
    renderHeaderNavigation();
}

function refreshWindowToggleCards() {
    for (const card of elements.windowList.querySelectorAll(".window-card")) {
        const h = Number(card.dataset.handle);
        card.classList.toggle("loop-active", state.loopHandles.has(h));
    }
}

function switchViewMode(mode) {
    if (state.viewMode === mode) return;
    state.viewMode = mode;
    elements.modeWindowButton.classList.toggle("active", mode === "window");
    elements.modeMonitorButton.classList.toggle("active", mode === "monitor");

    const navWindows = getNavigableWindows();
    if (navWindows.length > 0) {
        const current = navWindows.find((w) => w.handle === state.selectedHandle);
        if (!current) {
            selectWindow(navWindows[0].handle, false).catch(showTransientError);
        }
    }
    renderHeaderNavigation();
}

function applyLoopSelectionChange() {
    saveLoopSelection();
    refreshWindowToggleCards();
    const navWindows = getNavigableWindows();
    if (navWindows.length > 0 && !navWindows.some((w) => w.handle === state.selectedHandle)) {
        selectWindow(navWindows[0].handle, false).catch(showTransientError);
    } else if (navWindows.length === 0 && state.selectedHandle != null && state.selectedHandle >= 0) {
        resetSelection();
    }
    renderHeaderNavigation();
}

async function selectWindow(handle, closeMenuAfterSelection = true) {
    const selectionId = ++state.selectionGeneration;
    const canSwitchLiveStream = canSwitchActiveWebRtcStreamWithoutReconnect();

    if (!canSwitchLiveStream && state.selectedHandle && state.selectedHandle !== handle) {
        await disconnectWebRtcSession({ clearVideo: true });
    }

    if (selectionId !== state.selectionGeneration) {
        return;
    }

    const response = await fetch(`/api/windows/${handle}`, { credentials: "same-origin" });
    if (!response.ok) {
        throw new Error("Failed to load the selected window.");
    }

    if (selectionId !== state.selectionGeneration) {
        return;
    }

    state.selectedHandle = handle;
    state.selectedWindow = await response.json();
    saveLastWindowPreference(handle);
    renderSelection();
    highlightSelectedWindow();
    if (canSwitchLiveStream) {
        elements.viewerStatus.textContent = "Switching window without reconnecting...";
        await updateActiveStreamTarget(true);
    } else {
        startFrameLoop();
    }

    if (closeMenuAfterSelection) {
        closeDrawer();
    }
}

function renderSelection() {
    if (!state.selectedWindow) {
        resetSelection();
        return;
    }

    clearInteractionState(false);
    state.cursorRatio = { x: 0.5, y: 0.5 };
    state.cursorStyle = "mouse";
    state.frameScale = 1;
    state.frameAutoScale = 1;
    state.frameTranslateX = 0;
    state.frameTranslateY = 0;

    updateSelectedWindowPresentation();
    elements.activateButton.disabled = false;
    elements.frameRefreshButton.disabled = false;
    elements.resizeToggleButton.disabled = (state.selectedHandle < 0);
    elements.textInput.disabled = false;
    elements.textSubmit.disabled = false;
    for (const button of elements.quickKeyButtons) {
        button.disabled = false;
    }
    renderHeaderNavigation();
    syncFrameTransform();
    syncFrameCursor();
}

function updateSelectedWindowPresentation() {
    if (!state.selectedWindow) {
        return;
    }

    const title = state.selectedWindow.title;
    const meta = `${state.selectedWindow.processName} · ${state.selectedWindow.bounds.width} x ${state.selectedWindow.bounds.height}`;
    elements.selectedTitle.textContent = title;
    elements.selectedMeta.textContent = meta;
    elements.viewerWindowTitle.textContent = title;
    updateAutoFrameScale();
    renderHeaderNavigation();
    syncFrameTransform();
    syncFrameCursor();
}

function resetSelection() {
    clearInteractionState(false);
    clearPendingSingleTap(false);
    finishScrollPadInteraction(state.scrollPadPointerId, false);
    disconnectWebRtcSession({ clearVideo: true });
    state.selectedHandle = null;
    state.selectedWindow = null;
    state.cursorRatio = null;
    state.cursorStyle = "mouse";
    state.frameScale = 1;
    state.frameAutoScale = 1;
    state.frameTranslateX = 0;
    state.frameTranslateY = 0;
    state.windows = [];
    state.frameVideoWidth = 0;
    state.frameVideoHeight = 0;
    state.moveRequestInFlight = false;
    state.queuedMoveRatio = null;
    elements.selectedTitle.textContent = "No window selected";
    elements.selectedMeta.textContent = "左の一覧から選択してください。";
    elements.viewerWindowTitle.textContent = "No window selected";
    elements.activateButton.disabled = true;
    elements.frameRefreshButton.disabled = true;
    elements.resizeToggleButton.disabled = true;
    elements.resizeToggleButton.classList.remove("active");
    state.savedWindowBounds = null;
    elements.textInput.disabled = true;
    elements.textSubmit.disabled = true;
    for (const button of elements.quickKeyButtons) {
        button.disabled = true;
    }
    elements.windowFrame.hidden = true;
    elements.frameCursor.hidden = true;
    elements.framePlaceholder.hidden = false;
    elements.framePlaceholder.textContent = "WebRTC preview will appear here.";
    elements.viewerStatus.textContent = "No connection.";
    renderHeaderNavigation();
    syncFrameTransform();
    releaseFrameUrl();
}

function streamModeLabel(mode) {
    return mode === "high-quality"
        ? "Quality"
        : mode === "low-latency"
            ? "Speed"
            : "Balance";
}

function videoCodecLabel(codec) {
    return codec === "vp8"
        ? "VP8"
        : codec === "vp9"
            ? "VP9"
            : "Auto";
}

async function handleStreamModeChange(mode) {
    const nextMode = normalizeStreamMode(mode);
    if (nextMode === state.streamMode) {
        renderStreamModeSelection();
        return;
    }

    const previousRequestedCodec = ensureSupportedVideoCodecPreference(state.videoCodecPreference);
    const previousEffectiveCodec = resolveEffectiveVideoCodecPreference(previousRequestedCodec);
    state.streamMode = nextMode;
    renderStreamModeSelection();
    renderVideoCodecSelection();
    saveStreamModePreference(nextMode);
    const nextEffectiveCodec = resolveEffectiveVideoCodecPreference(previousRequestedCodec);
    elements.viewerStatus.textContent = `Stream mode: ${streamModeLabel(nextMode)} · ${describeCodecPreference(previousRequestedCodec, nextEffectiveCodec)}`;
    if (state.selectedHandle && !state.pointerActive) {
        await refreshFrameNow(previousEffectiveCodec !== nextEffectiveCodec);
    }
}

async function handleVideoCodecChange(codec) {
    const previousRequestedCodec = ensureSupportedVideoCodecPreference(state.videoCodecPreference);
    const previousEffectiveCodec = resolveEffectiveVideoCodecPreference(previousRequestedCodec);
    const nextCodec = ensureSupportedVideoCodecPreference(codec);
    if (nextCodec === state.videoCodecPreference) {
        renderVideoCodecSelection();
        return;
    }

    state.videoCodecPreference = nextCodec;
    renderVideoCodecSelection();
    saveVideoCodecPreference(nextCodec);
    const nextEffectiveCodec = resolveEffectiveVideoCodecPreference(nextCodec);
    elements.viewerStatus.textContent = `Codec preference: ${describeCodecPreference(nextCodec, nextEffectiveCodec)}`;
    if (state.selectedHandle && !state.pointerActive) {
        await refreshFrameNow(previousEffectiveCodec !== nextEffectiveCodec);
    }
}

function highlightSelectedWindow() {
    for (const card of document.querySelectorAll(".window-card")) {
        card.classList.toggle("selected", Number(card.dataset.handle) === state.selectedHandle);
    }
}

function getNavigableWindows() {
    if (state.viewMode === "monitor") {
        return state.windows.filter((w) => w.handle < 0);
    }
    return state.windows.filter((w) => w.handle >= 0 && state.loopHandles.has(w.handle));
}

function renderHeaderNavigation() {
    const navWindows = getNavigableWindows();
    const inNav = navWindows.some((w) => w.handle === state.selectedHandle);
    const canNav = (inNav && navWindows.length > 1) || (!inNav && navWindows.length > 0 && state.selectedHandle != null);
    elements.windowPrevButton.disabled = !canNav;
    elements.windowNextButton.disabled = !canNav;
}

async function selectAdjacentWindow(direction) {
    const navWindows = getNavigableWindows();
    if (!state.selectedHandle || navWindows.length === 0) {
        return;
    }

    const currentIndex = navWindows.findIndex((w) => w.handle === state.selectedHandle);
    if (currentIndex < 0) {
        await selectWindow(navWindows[0].handle, false);
        return;
    }

    const nextIndex = (currentIndex + direction + navWindows.length) % navWindows.length;
    if (nextIndex === currentIndex) {
        return;
    }

    await selectWindow(navWindows[nextIndex].handle, false);
}

function startFrameLoop() {
    stopFrameLoop();
    refreshFrameNow(true).catch(showTransientError);
}

function stopFrameLoop() {
    if (state.frameTimer) {
        window.clearTimeout(state.frameTimer);
        state.frameTimer = null;
    }
}

function scheduleNextFrame(delayMs) {
    if (!state.selectedHandle || state.webrtcConnected || state.frameBusy) {
        return;
    }

    stopFrameLoop();
    state.frameTimer = window.setTimeout(() => {
        refreshFrameNow(false).catch(showTransientError);
    }, delayMs);
}

function getFrameIntervalMs() {
    return Math.max(1, Math.round(1000 / state.frameRate));
}

function getNextFrameDelay(startedAt) {
    const elapsed = performance.now() - startedAt;
    return Math.max(0, getFrameIntervalMs() - elapsed);
}

function canSwitchActiveWebRtcStreamWithoutReconnect() {
    return Boolean(
        state.selectedHandle
        && state.webrtcConnected
        && state.peerConnection
        && state.peerConnection.connectionState === "connected"
        && state.signalingSocket
        && state.signalingSocket.readyState === WebSocket.OPEN
    );
}

function scheduleActiveStreamTargetSync() {
    if (!canSwitchActiveWebRtcStreamWithoutReconnect()) {
        return;
    }

    if (state.streamTargetSyncTimer) {
        window.clearTimeout(state.streamTargetSyncTimer);
    }

    state.streamTargetSyncTimer = window.setTimeout(() => {
        state.streamTargetSyncTimer = null;
        updateActiveStreamTarget(false).catch(showTransientError);
    }, 160);
}

async function updateActiveStreamTarget(force) {
    if (!canSwitchActiveWebRtcStreamWithoutReconnect()) {
        return false;
    }

    const requestedWidth = getRequestedFrameWidth();
    const handleChanged = state.activeStreamHandle !== state.selectedHandle;
    const widthChanged = state.activeStreamMaxWidth !== requestedWidth;
    if (!force && !handleChanged && !widthChanged) {
        return false;
    }

    const payload = {
        type: handleChanged ? "switch-window" : "update-stream",
        handle: state.selectedHandle,
        maxWidth: requestedWidth,
    };

    state.signalingSocket.send(JSON.stringify(payload));
    state.activeStreamHandle = state.selectedHandle;
    state.activeStreamMaxWidth = requestedWidth;
    logClientEvent("info", "Updated active WebRTC capture target.", payload);
    return true;
}

async function refreshFrameNow(force) {
    if (!state.selectedHandle || state.frameBusy) {
        return;
    }

    state.frameBusy = true;
    try {
        await ensureWebRtcSession(force);
    } finally {
        state.frameBusy = false;
    }
}

function releaseFrameUrl() {
    if (state.frameUrl) {
        URL.revokeObjectURL(state.frameUrl);
        state.frameUrl = null;
    }
}

async function ensureWebRtcSession(forceReconnect) {
    if (!state.selectedHandle) {
        return;
    }

    const selectedCodec = ensureSupportedVideoCodecPreference(state.videoCodecPreference);
    const effectiveCodec = resolveEffectiveVideoCodecPreference(selectedCodec);
    const requestedWidth = getRequestedFrameWidth();
    const currentKey = `${state.frameRate}:${state.streamMode}:${effectiveCodec}`;

    const isActive = state.webrtcConnected
        && state.peerConnection
        && state.signalingSocket
        && state.signalingSocket.readyState === WebSocket.OPEN
        && state.peerConnection.connectionState !== "closed"
        && state.peerConnection.__streamKey === currentKey;

    if (!forceReconnect && isActive) {
        scheduleActiveStreamTargetSync();
        return;
    }

    const hadSession = Boolean(state.peerConnection || state.signalingSocket);
    await disconnectWebRtcSession({ clearVideo: forceReconnect });
    if (hadSession) {
        await wait(WEBRTC_RECONNECT_COOLDOWN_MS);
    }
    await connectWebRtcSession(requestedWidth, currentKey, selectedCodec, effectiveCodec);
}

async function connectWebRtcSession(requestedWidth, streamKey, selectedCodec, effectiveCodec) {
    if (!state.selectedHandle) {
        return;
    }

    logClientEvent("info", "Starting WebRTC session.", {
        handle: state.selectedHandle,
        frameRate: state.frameRate,
        maxWidth: requestedWidth,
        streamMode: state.streamMode,
        requestedCodec: selectedCodec,
        effectiveCodec,
    });

    const generation = ++state.webrtcGeneration;
    const peerConnection = new RTCPeerConnection({
        iceServers: [],
    });
    const signalingSocket = new WebSocket(buildWebRtcUrl(state.selectedHandle, requestedWidth, state.frameRate, state.streamMode, effectiveCodec));
    const pendingRemoteIceCandidates = [];
    let remoteDescriptionApplied = false;
    let signalingMessageChain = Promise.resolve();
    const videoTransceiver = peerConnection.addTransceiver("video", { direction: "recvonly" });
    configureReceiverForCurrentMode(videoTransceiver.receiver);
    applyVideoCodecPreference(videoTransceiver, effectiveCodec, selectedCodec);

    peerConnection.__streamKey = streamKey;
    state.peerConnection = peerConnection;
    state.signalingSocket = signalingSocket;
    state.webrtcConnected = false;
    state.activeStreamHandle = state.selectedHandle;
    state.activeStreamMaxWidth = requestedWidth;
    elements.viewerStatus.textContent = `WebRTC connecting... ${state.frameRate} fps · ${describeCodecPreference(selectedCodec, effectiveCodec)}`;
    elements.framePlaceholder.hidden = false;
    elements.framePlaceholder.textContent = "WebRTC stream connecting...";
    elements.windowFrame.hidden = true;

    const connected = new Promise((resolve, reject) => {
        let settled = false;

        const resolveOnce = () => {
            if (!settled) {
                settled = true;
                resolve();
            }
        };
        const rejectOnce = (error) => {
            if (!settled) {
                settled = true;
                reject(error);
            }
        };

        signalingSocket.addEventListener("open", async () => {
            logClientEvent("info", "WebRTC signaling socket opened.", {
                handle: state.selectedHandle,
                readyState: signalingSocket.readyState,
            });
            if (generation !== state.webrtcGeneration) {
                rejectOnce(new Error("Stale WebRTC session."));
                return;
            }

            try {
                const offer = await peerConnection.createOffer({
                    offerToReceiveAudio: false,
                    offerToReceiveVideo: true,
                });
                logClientEvent("info", "Created local WebRTC offer.", {
                    type: offer?.type ?? null,
                    sdpLength: offer?.sdp?.length ?? 0,
                });
                await peerConnection.setLocalDescription(offer);
                logClientEvent("info", "Local WebRTC offer applied.", {
                    signalingState: peerConnection.signalingState,
                });
                await waitForIceGatheringComplete(peerConnection);
                const localDescription = peerConnection.localDescription;
                if (!localDescription || !localDescription.sdp) {
                    throw new Error("Local offer was null after setLocalDescription.");
                }
                logClientEvent("info", "Sending local WebRTC offer.", {
                    type: localDescription.type,
                    sdpLength: localDescription.sdp.length,
                });
                signalingSocket.send(JSON.stringify(localDescription));
            } catch (error) {
                logClientEvent("error", "Failed to create/send local WebRTC offer.", {
                    message: error?.message || String(error),
                });
                rejectOnce(normalizeError(error, "Failed to create a WebRTC offer."));
            }
        }, { once: true });

        const processSignalingMessage = async (event) => {
            try {
                const signal = JSON.parse(event.data);
                logClientEvent("info", "Received signaling payload.", {
                    type: signal?.type ?? null,
                    hasSdp: Boolean(signal?.sdp),
                    hasCandidate: Boolean(signal?.candidate),
                });
                if (signal.candidate) {
                    if (remoteDescriptionApplied) {
                        await peerConnection.addIceCandidate(signal);
                        logClientEvent("info", "Applied remote ICE candidate.", {
                            sdpMid: signal.sdpMid ?? null,
                            sdpMLineIndex: signal.sdpMLineIndex ?? null,
                        });
                    } else {
                        pendingRemoteIceCandidates.push(signal);
                        logClientEvent("info", "Queued remote ICE candidate until remote description is ready.", {
                            queuedCount: pendingRemoteIceCandidates.length,
                            sdpMid: signal.sdpMid ?? null,
                            sdpMLineIndex: signal.sdpMLineIndex ?? null,
                        });
                    }
                    return;
                }

                if (signal.sdp) {
                    await peerConnection.setRemoteDescription(signal);
                    remoteDescriptionApplied = true;
                    logClientEvent("info", "Applied remote session description.", {
                        type: signal.type ?? null,
                        sdpLength: signal.sdp.length,
                    });
                    while (pendingRemoteIceCandidates.length > 0) {
                        const candidate = pendingRemoteIceCandidates.shift();
                        await peerConnection.addIceCandidate(candidate);
                    }
                    if (pendingRemoteIceCandidates.length === 0) {
                        logClientEvent("info", "Flushed queued remote ICE candidates after remote description.");
                    }
                    return;
                }

                logClientEvent("warning", "Received signaling payload without SDP/candidate.", {
                    payload: event.data,
                });
            } catch (error) {
                logClientEvent("error", "Failed to process WebRTC signaling.", {
                    payload: event.data,
                    message: error?.message || String(error),
                });
                rejectOnce(normalizeError(error, "Failed to process WebRTC signaling."));
            }
        };

        signalingSocket.addEventListener("message", (event) => {
            signalingMessageChain = signalingMessageChain
                .then(() => processSignalingMessage(event))
                .catch((error) => {
                    logClientEvent("error", "Failed to process queued WebRTC signaling.", {
                        message: error?.message || String(error),
                    });
                    rejectOnce(normalizeError(error, "Failed to process queued WebRTC signaling."));
                });
        });

        signalingSocket.addEventListener("close", (event) => {
            logClientEvent("warning", "WebRTC signaling socket closed.", {
                code: event.code,
                reason: event.reason,
                wasClean: event.wasClean,
            });
            rejectOnce(new Error("WebRTC signaling closed before the stream was ready."));
        }, { once: true });

        signalingSocket.addEventListener("error", () => {
            logClientEvent("error", "WebRTC signaling socket error.");
            rejectOnce(new Error("WebRTC signaling failed."));
        }, { once: true });

        peerConnection.addEventListener("track", async (event) => {
            logClientEvent("info", "Received remote media track.", {
                kind: event.track?.kind ?? null,
                streamCount: event.streams?.length ?? 0,
            });
            configureReceiverForCurrentMode(event.receiver || videoTransceiver.receiver);
            if (generation !== state.webrtcGeneration) {
                return;
            }

            const stream = event.streams && event.streams[0]
                ? event.streams[0]
                : new MediaStream([event.track]);
            if (elements.windowFrame.srcObject !== stream) {
                elements.windowFrame.srcObject = stream;
            }
            elements.windowFrame.hidden = false;
            elements.framePlaceholder.hidden = true;

            try {
                await elements.windowFrame.play();
            } catch {
            }
        });

        elements.windowFrame.addEventListener("loadedmetadata", async () => {
            logClientEvent("info", "Remote video metadata loaded.", {
                width: elements.windowFrame.videoWidth,
                height: elements.windowFrame.videoHeight,
            });
            if (generation !== state.webrtcGeneration) {
                return;
            }

            elements.windowFrame.hidden = false;
            elements.framePlaceholder.hidden = true;
            try {
                await elements.windowFrame.play();
            } catch {
            }
        }, { once: true });

        peerConnection.addEventListener("icegatheringstatechange", () => {
            logClientEvent("info", "ICE gathering state changed.", {
                state: peerConnection.iceGatheringState,
            });
        });

        peerConnection.addEventListener("iceconnectionstatechange", () => {
            logClientEvent("info", "ICE connection state changed.", {
                state: peerConnection.iceConnectionState,
            });
        });

        peerConnection.addEventListener("signalingstatechange", () => {
            logClientEvent("info", "WebRTC signaling state changed.", {
                state: peerConnection.signalingState,
            });
        });

        peerConnection.addEventListener("connectionstatechange", () => {
            logClientEvent("info", "Peer connection state changed.", {
                state: peerConnection.connectionState,
            });
            if (generation !== state.webrtcGeneration) {
                return;
            }

            const connectionState = peerConnection.connectionState;
            if (connectionState === "connected") {
                state.webrtcConnected = true;
                elements.windowFrame.hidden = false;
                elements.framePlaceholder.hidden = true;
                elements.viewerStatus.textContent = `WebRTC live · ${state.frameRate} fps · ${streamModeLabel(state.streamMode)} · ${describeCodecPreference(selectedCodec, effectiveCodec)}`;
                updateFrameCursor(state.cursorRatio ?? { x: 0.5, y: 0.5 }, { style: cursorStyleForCurrentState(), pressed: isPressedCursor() });
                syncFrameTransform();
                resolveOnce();
                return;
            }

            if (connectionState === "failed" || connectionState === "closed" || connectionState === "disconnected") {
                state.webrtcConnected = false;
                if (generation === state.webrtcGeneration) {
                    elements.viewerStatus.textContent = "WebRTC disconnected.";
                }
            }
        });
    });

    try {
        await connected;
    } catch (error) {
        if (generation === state.webrtcGeneration) {
            logClientEvent("error", "Failed to establish the WebRTC stream.", {
                message: error?.message || String(error),
            });
            await disconnectWebRtcSession({ clearVideo: true });
            throw normalizeError(error, "Failed to establish the WebRTC stream.");
        }
    }
}

async function disconnectWebRtcSession(options = {}) {
    state.webrtcGeneration += 1;
    state.webrtcConnected = false;
    stopFrameLoop();
    if (state.streamTargetSyncTimer) {
        window.clearTimeout(state.streamTargetSyncTimer);
        state.streamTargetSyncTimer = null;
    }
    state.activeStreamHandle = null;
    state.activeStreamMaxWidth = null;

    const peerConnection = state.peerConnection;
    const signalingSocket = state.signalingSocket;
    state.peerConnection = null;
    state.signalingSocket = null;

    if (peerConnection) {
        try {
            peerConnection.ontrack = null;
        } catch {
        }
        try {
            peerConnection.close();
        } catch {
        }
    }

    if (signalingSocket) {
        try {
            if (signalingSocket.readyState === WebSocket.OPEN || signalingSocket.readyState === WebSocket.CONNECTING) {
                signalingSocket.close(1000, "switch");
            }
        } catch {
        }
    }

    if (options.clearVideo) {
        if (elements.windowFrame.srcObject instanceof MediaStream) {
            for (const track of elements.windowFrame.srcObject.getTracks()) {
                track.stop();
            }
        }
        elements.windowFrame.srcObject = null;
        elements.windowFrame.hidden = true;
        elements.framePlaceholder.hidden = false;
    }
}

function getRequestedFrameWidth() {
    const frameWidth = Math.max(elements.frameStage.clientWidth || 0, elements.windowFrame.clientWidth || 0, 480);
    const modeCaps = FRAME_WIDTH_CAPS_BY_MODE[state.streamMode] || FRAME_WIDTH_CAPS_BY_MODE[DEFAULT_STREAM_MODE];
    const devicePixelRatioCap = FRAME_DEVICE_PIXEL_RATIO_CAP_BY_MODE[state.streamMode] || FRAME_DEVICE_PIXEL_RATIO_CAP_BY_MODE[DEFAULT_STREAM_MODE];
    const devicePixelRatio = Math.max(1, Math.min(window.devicePixelRatio || 1, devicePixelRatioCap));
    const requestedWidth = Math.round(frameWidth * devicePixelRatio);
    const widthCap = modeCaps[state.frameRate] || modeCaps[DEFAULT_FRAME_RATE];
    return Math.max(480, Math.min(widthCap, requestedWidth));
}

function configureReceiverForCurrentMode(receiver) {
    if (!receiver) {
        return;
    }

    const lowLatency = state.streamMode === "low-latency";
    const playoutDelayHint = lowLatency ? 0 : state.streamMode === "balanced" ? 0.03 : 0.08;
    const jitterBufferTarget = lowLatency ? 0 : state.streamMode === "balanced" ? 30 : 80;

    try {
        if ("playoutDelayHint" in receiver) {
            receiver.playoutDelayHint = playoutDelayHint;
        }
    } catch {
    }

    try {
        if ("jitterBufferTarget" in receiver) {
            receiver.jitterBufferTarget = jitterBufferTarget;
        }
    } catch {
    }
}

function applyVideoCodecPreference(transceiver, codecPreference, requestedCodecPreference = codecPreference) {
    if (!transceiver || typeof transceiver.setCodecPreferences !== "function" || !window.RTCRtpReceiver || typeof RTCRtpReceiver.getCapabilities !== "function") {
        return;
    }

    const capabilities = RTCRtpReceiver.getCapabilities("video");
    const codecs = Array.isArray(capabilities?.codecs) ? capabilities.codecs.slice() : [];
    if (codecs.length === 0) {
        return;
    }

    const options = getVideoCodecUiOptions();
    const supportedServerCodecs = options
        .filter((option) => option.available && option.value !== "auto")
        .map((option) => option.value);
    const preferredToken = codecPreference !== "auto" && supportedServerCodecs.includes(codecPreference)
        ? codecPreference
        : null;
    const preferred = [];
    const fallback = [];
    for (const codec of codecs) {
        const mimeType = typeof codec?.mimeType === "string" ? codec.mimeType.toLowerCase() : "";
        if (preferredToken && codecMatchesPreference(codec, preferredToken)) {
            preferred.push(codec);
        } else if (supportedServerCodecs.some((value) => mimeType.includes(value))) {
            fallback.push(codec);
        } else {
            fallback.push(codec);
        }
    }

    const ordered = preferred.length > 0 ? preferred.concat(fallback) : codecs;
    try {
        transceiver.setCodecPreferences(ordered);
        logClientEvent("info", "Applied video codec preference.", {
            requested: requestedCodecPreference,
            effective: codecPreference,
            preferredCount: preferred.length,
            codecOrder: ordered.map((codec) => ({
                mimeType: codec?.mimeType || null,
                fmtp: codec?.sdpFmtpLine || null,
            })),
        });
    } catch (error) {
        logClientEvent("warning", "Failed to apply video codec preference.", {
            requested: requestedCodecPreference,
            effective: codecPreference,
            message: error?.message || String(error),
        });
    }
}

function codecMatchesPreference(codec, codecPreference) {
    const mimeType = typeof codec?.mimeType === "string" ? codec.mimeType.toLowerCase() : "";
    if (!mimeType.includes(codecPreference)) {
        return false;
    }

    const fmtp = typeof codec?.sdpFmtpLine === "string" ? codec.sdpFmtpLine.toLowerCase() : "";
    switch (codecPreference) {
        case "vp9":
            return fmtp.length === 0 || fmtp.includes("profile-id=0");
        default:
            return true;
    }
}

function resolveEffectiveVideoCodecPreference(codecPreference, options = getVideoCodecUiOptions()) {
    const normalized = ensureSupportedVideoCodecPreference(codecPreference, options);
    if (normalized !== "auto") {
        return normalized;
    }

    const vp9Available = options.some((option) => option.value === "vp9" && option.available !== false);
    if (!vp9Available) {
        return "vp8";
    }

    if (state.streamMode === "low-latency") {
        return "vp8";
    }

    return state.frameRate <= 30 ? "vp9" : "vp8";
}

function describeCodecPreference(requestedCodec, effectiveCodec) {
    return requestedCodec === "auto"
        ? `Auto→${videoCodecLabel(effectiveCodec)}`
        : videoCodecLabel(effectiveCodec);
}

function buildWebRtcUrl(handle, maxWidth, frameRate, streamMode, codecPreference) {
    const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
    const url = new URL(`${protocol}//${window.location.host}/ws/webrtc`);
    url.searchParams.set("handle", String(handle));
    url.searchParams.set("maxWidth", String(maxWidth));
    url.searchParams.set("frameRate", String(frameRate));
    url.searchParams.set("mode", normalizeStreamMode(streamMode));
    url.searchParams.set("codec", ensureSupportedVideoCodecPreference(codecPreference));
    return url.toString();
}

function registerGlobalClientLogging() {
    window.addEventListener("error", (event) => {
        logClientEvent("error", event.message || "Unhandled window error.", {
            filename: event.filename || null,
            lineno: event.lineno || null,
            colno: event.colno || null,
        });
    });

    window.addEventListener("unhandledrejection", (event) => {
        logClientEvent("error", "Unhandled promise rejection.", {
            reason: stringifyErrorLike(event.reason),
        });
    });
}

function logClientEvent(level, message, context = null) {
    const payload = {
        level,
        source: "browser",
        message,
        context: sanitizeLogValue(context),
    };

    fetch("/api/client-log", {
        method: "POST",
        credentials: "same-origin",
        headers: {
            "Content-Type": "application/json",
        },
        body: JSON.stringify(payload),
    }).catch(() => {
    });
}

function sanitizeLogValue(value) {
    if (value === undefined) {
        return null;
    }

    try {
        return JSON.parse(JSON.stringify(value, (_, current) => {
            if (current instanceof Error) {
                return {
                    name: current.name,
                    message: current.message,
                    stack: current.stack,
                };
            }

            return current;
        }));
    } catch {
        return {
            value: stringifyErrorLike(value),
        };
    }
}

function stringifyErrorLike(value) {
    if (value instanceof Error) {
        return `${value.name}: ${value.message}`;
    }

    if (typeof value === "string") {
        return value;
    }

    try {
        return JSON.stringify(value);
    } catch {
        return String(value);
    }
}

async function handleActivateWindow() {
    if (!state.selectedHandle) {
        return;
    }

    const response = await fetch(`/api/windows/${state.selectedHandle}/activate`, {
        method: "POST",
        credentials: "same-origin",
    });

    if (!response.ok) {
        throw new Error(await readErrorMessage(response));
    }

    elements.viewerStatus.textContent = "Activation requested.";
    await refreshWindows(true);
    await refreshFrameNow(true);
}

function handleFrameRateChange(event) {
    const nextRate = normalizeFrameRate(Number(event.target.value));
    if (nextRate === state.frameRate) {
        renderFrameRateSelection();
        return;
    }

    const previousRequestedCodec = ensureSupportedVideoCodecPreference(state.videoCodecPreference);
    const previousEffectiveCodec = resolveEffectiveVideoCodecPreference(previousRequestedCodec);
    state.frameRate = nextRate;
    renderFrameRateSelection();
    renderVideoCodecSelection();
    saveFrameRatePreference(nextRate);
    const nextEffectiveCodec = resolveEffectiveVideoCodecPreference(previousRequestedCodec);
    elements.viewerStatus.textContent = `Frame rate: ${nextRate} fps · ${describeCodecPreference(previousRequestedCodec, nextEffectiveCodec)}`;
    if (state.selectedHandle && !state.pointerActive) {
        refreshFrameNow(previousEffectiveCodec !== nextEffectiveCodec).catch(showTransientError);
    }
}

function normalizeFrameRate(value) {
    return FRAME_RATE_OPTIONS.includes(value) ? value : DEFAULT_FRAME_RATE;
}

function normalizeStreamMode(value) {
    return STREAM_MODE_OPTIONS.includes(value) ? value : DEFAULT_STREAM_MODE;
}

function normalizeVideoCodecPreference(value) {
    return VIDEO_CODEC_OPTIONS.includes(value) ? value : DEFAULT_VIDEO_CODEC;
}

function getVideoCodecUiOptions() {
    return Array.isArray(state.serverInfo?.videoCodecOptions) && state.serverInfo.videoCodecOptions.length > 0
        ? state.serverInfo.videoCodecOptions.map((option) => ({
            value: normalizeVideoCodecPreference(option?.value),
            label: typeof option?.label === "string" && option.label ? option.label : videoCodecLabel(option?.value),
            available: option?.available !== false,
            hint: typeof option?.hint === "string" && option.hint ? option.hint : "",
        }))
        : DEFAULT_VIDEO_CODEC_UI_OPTIONS;
}

function ensureSupportedVideoCodecPreference(value, options = getVideoCodecUiOptions()) {
    const normalized = normalizeVideoCodecPreference(value);
    const selectedOption = options.find((option) => option.value === normalized);
    if (selectedOption?.available !== false) {
        return normalized;
    }

    if (state.videoCodecPreference !== DEFAULT_VIDEO_CODEC) {
        state.videoCodecPreference = DEFAULT_VIDEO_CODEC;
        saveVideoCodecPreference(DEFAULT_VIDEO_CODEC);
    }

    return DEFAULT_VIDEO_CODEC;
}

function loadFrameRatePreference() {
    try {
        const stored = Number(window.localStorage.getItem(FRAME_RATE_STORAGE_KEY));
        return normalizeFrameRate(stored);
    } catch {
        return DEFAULT_FRAME_RATE;
    }
}

function saveFrameRatePreference(value) {
    try {
        window.localStorage.setItem(FRAME_RATE_STORAGE_KEY, String(normalizeFrameRate(value)));
    } catch {
    }
}

function loadStreamModePreference() {
    try {
        return normalizeStreamMode(window.localStorage.getItem(STREAM_MODE_STORAGE_KEY));
    } catch {
        return DEFAULT_STREAM_MODE;
    }
}

function saveStreamModePreference(value) {
    try {
        window.localStorage.setItem(STREAM_MODE_STORAGE_KEY, normalizeStreamMode(value));
    } catch {
    }
}

function loadVideoCodecPreference() {
    try {
        return normalizeVideoCodecPreference(window.localStorage.getItem(VIDEO_CODEC_STORAGE_KEY));
    } catch {
        return DEFAULT_VIDEO_CODEC;
    }
}

function saveVideoCodecPreference(value) {
    try {
        window.localStorage.setItem(VIDEO_CODEC_STORAGE_KEY, ensureSupportedVideoCodecPreference(value));
    } catch {
    }
}


function loadLoopSelection() {
    try {
        const raw = window.localStorage.getItem(LOOP_SELECTION_STORAGE_KEY);
        if (!raw) return null;
        const parsed = JSON.parse(raw);
        if (Array.isArray(parsed)) return parsed;
        return null;
    } catch {
        return null;
    }
}

function saveLoopSelection() {
    try {
        window.localStorage.setItem(LOOP_SELECTION_STORAGE_KEY, JSON.stringify([...state.loopHandles]));
    } catch {
    }
}

function loadTouchModePreference() {
    try {
        const v = window.localStorage.getItem(TOUCH_MODE_STORAGE_KEY);
        return v === "pointer" ? "pointer" : "direct";
    } catch {
        return "direct";
    }
}

function saveTouchModePreference(mode) {
    try {
        window.localStorage.setItem(TOUCH_MODE_STORAGE_KEY, mode);
    } catch {
    }
}

function setTouchMode(mode) {
    state.touchMode = mode;
    saveTouchModePreference(mode);
    applyTouchModeUI();
}

function applyTouchModeUI() {
    const isDirect = state.touchMode === "direct";
    elements.touchModePointerButton.classList.toggle("active", !isDirect);
    elements.touchModeDirectButton.classList.toggle("active", isDirect);
    elements.scrollPad.style.display = isDirect ? "none" : "";
    elements.scrollPadLeft.style.display = isDirect ? "none" : "";
    elements.frameCursor.hidden = isDirect || !state.cursorRatio;
}

function loadResizeScalePreference() {
    try {
        const stored = Number(window.localStorage.getItem(RESIZE_SCALE_STORAGE_KEY));
        return [1, 1.5, 2, 3, 4].includes(stored) ? stored : 3;
    } catch {
        return 3;
    }
}

function saveResizeScalePreference(value) {
    try {
        window.localStorage.setItem(RESIZE_SCALE_STORAGE_KEY, String(value));
    } catch {
    }
}

function loadLastWindowPreference() {
    try {
        const stored = window.localStorage.getItem(LAST_WINDOW_STORAGE_KEY);
        return stored ? Number(stored) : null;
    } catch {
        return null;
    }
}

function saveLastWindowPreference(handle) {
    try {
        window.localStorage.setItem(LAST_WINDOW_STORAGE_KEY, String(handle));
    } catch {
    }
}

function renderResizeScaleSelection() {
    if (elements.resizeScaleSelect) {
        elements.resizeScaleSelect.value = String(state.resizeScale);
    }
}

function handleResizeScaleChange() {
    const value = Number(elements.resizeScaleSelect.value);
    state.resizeScale = value;
    saveResizeScalePreference(value);
}

async function handleFramePointerDown(event) {
    if (!state.selectedHandle || elements.windowFrame.hidden || event.pointerType === "touch") {
        return;
    }

    event.preventDefault();
    state.pointerActive = true;
    state.activePointerId = event.pointerId;
    state.activeInputKind = event.pointerType || "mouse";
    state.pointerLastRatio = getFrameRatiosFromClient(event.clientX, event.clientY) ?? state.cursorRatio ?? { x: 0.5, y: 0.5 };
    stopFrameLoop();

    if (typeof elements.windowFrame.setPointerCapture === "function") {
        elements.windowFrame.setPointerCapture(event.pointerId);
    }

    updateFrameCursor(state.pointerLastRatio, { style: cursorStyleForCurrentState(), pressed: true });
    await sendPointerAction("down", state.pointerLastRatio);
    elements.viewerStatus.textContent = "Left button down.";
}

function handleFramePointerCancel(event) {
    if (event.pointerType === "touch" || state.activePointerId !== event.pointerId) {
        return;
    }

    const lastRatio = state.pointerLastRatio ?? state.cursorRatio;
    if (state.activePointerId !== null && typeof elements.windowFrame.releasePointerCapture === "function") {
        try {
            elements.windowFrame.releasePointerCapture(state.activePointerId);
        } catch {
        }
    }

    clearInteractionState(false);
    updateFrameCursor(lastRatio, { style: cursorStyleForCurrentState(), pressed: false });

    if (lastRatio) {
        sendPointerAction("up", lastRatio).catch(showTransientError);
    }

    scheduleNextFrame(getFrameIntervalMs());
}

async function handleFramePointerUp(event) {
    if (!state.selectedHandle || elements.windowFrame.hidden || event.pointerType === "touch" || state.activePointerId !== event.pointerId) {
        return;
    }

    event.preventDefault();
    if (typeof elements.windowFrame.releasePointerCapture === "function") {
        try {
            elements.windowFrame.releasePointerCapture(event.pointerId);
        } catch {
        }
    }

    const ratio = getFrameRatiosFromClient(event.clientX, event.clientY) ?? state.pointerLastRatio ?? state.cursorRatio;
    clearInteractionState(false);

    if (!ratio) {
        scheduleNextFrame(getFrameIntervalMs());
        return;
    }

    updateFrameCursor(ratio, { style: cursorStyleForCurrentState(), pressed: false });

    await sendPointerAction("up", ratio);
    elements.viewerStatus.textContent = "Left click sent.";

    scheduleNextFrame(getFrameIntervalMs());
}

function handleFramePointerMove(event) {
    if (!state.selectedHandle || elements.windowFrame.hidden || event.pointerType === "touch") {
        return;
    }

    const ratio = getFrameRatiosFromClient(event.clientX, event.clientY);
    if (ratio) {
        updateFrameCursor(ratio, { style: cursorStyleForCurrentState(), pressed: state.pointerActive });
    }

    if (!state.pointerActive || state.activePointerId !== event.pointerId || !ratio) {
        return;
    }

    event.preventDefault();
    state.pointerLastRatio = ratio;
    queuePointerMove(ratio);
}

async function handleFrameTouchStart(event) {
    if (!state.selectedHandle || elements.windowFrame.hidden) {
        return;
    }

    if (state.touchMode === "direct") {
        handleDirectTouchStart(event);
        return;
    }

    if (event.touches.length >= 2) {
        event.preventDefault();
        await startOrUpdateTwoFingerScroll(event.touches);
        return;
    }

    if (event.touches.length !== 1) {
        return;
    }

    const touch = event.touches[0];
    event.preventDefault();
    stopFrameLoop();

    if (state.pendingTapTimer) {
        window.clearTimeout(state.pendingTapTimer);
        state.pendingTapTimer = null;
    }

    clearPendingTouch();
    state.pointerActive = true;
    state.activeInputKind = "touch";
    state.touchPendingId = touch.identifier;
    state.touchPendingPoint = pointFromTouch(touch);
    state.touchPendingMoved = false;
    state.pointerLastClientPoint = pointFromTouch(touch);
    state.touchInteractionMode = null;
    updateFrameCursor(state.cursorRatio ?? { x: 0.5, y: 0.5 }, { style: cursorStyleForCurrentState(), pressed: false });

    state.touchPendingTimer = window.setTimeout(() => {
        activateTouchHoldMode().catch(showTransientError);
    }, TOUCH_DRAG_HOLD_MS);
}

async function handleFrameTouchMove(event) {
    if (!state.selectedHandle || elements.windowFrame.hidden) {
        return;
    }

    if (state.touchMode === "direct") {
        handleDirectTouchMove(event);
        return;
    }

    if (event.touches.length >= 2) {
        event.preventDefault();
        await startOrUpdateTwoFingerScroll(event.touches);
        return;
    }

    const touch = findTrackedTouch(event.touches);
    if (!touch) {
        return;
    }

    event.preventDefault();
    const point = pointFromTouch(touch);
    const previousPoint = state.pointerLastClientPoint ?? point;
    const deltaX = point.x - previousPoint.x;
    const deltaY = point.y - previousPoint.y;
    state.pointerLastClientPoint = point;

    if (state.touchPendingId === touch.identifier) {
        const distance = distanceBetween(point, state.touchPendingPoint ?? point);
        if (distance >= TOUCH_MOVE_THRESHOLD) {
            flushPendingSingleTapNow();
            clearPendingTouch();
            state.touchPendingId = null;
            state.touchPendingPoint = null;
            state.activePointerId = touch.identifier;
            state.touchInteractionMode = "move";
            state.touchPendingMoved = true;
            elements.viewerStatus.textContent = "Cursor moving.";
        } else {
            return;
        }
    }

    if (state.activePointerId !== touch.identifier) {
        return;
    }

    if (state.touchInteractionMode === "scroll") {
        state.cursorStyle = "scroll";
        updateFrameCursor(state.cursorRatio, { style: "scroll", pressed: false });
        handleScrollDeltaPixels(deltaY);
        return;
    }

    const pressed = state.touchInteractionMode === "drag";
    applyRelativeCursorDelta(deltaX, deltaY, { style: cursorStyleForCurrentState(), pressed, sendMove: true });
}

async function handleFrameTouchEnd(event) {
    if (!state.selectedHandle || elements.windowFrame.hidden) {
        return;
    }

    if (state.touchMode === "direct") {
        handleDirectTouchEnd(event);
        return;
    }

    if (state.touchTwoFingerScroll) {
        event.preventDefault();
        if (event.touches.length < 2) {
            finishTwoFingerGesture();
        }
        return;
    }

    const pendingEnded = touchListContains(event.changedTouches, state.touchPendingId);
    const activeEnded = touchListContains(event.changedTouches, state.activePointerId);
    if (!pendingEnded && !activeEnded) {
        return;
    }

    event.preventDefault();

    if (pendingEnded) {
        const moved = state.touchPendingMoved;
        clearPendingTouch();
        state.pointerActive = false;
        state.activeInputKind = null;
        state.pointerLastClientPoint = null;
        state.touchPendingMoved = false;
        state.touchPendingId = null;
        state.touchPendingPoint = null;

        if (!moved && state.cursorRatio) {
            handleLeftTap(state.cursorRatio);
        }

        updateFrameCursor(state.cursorRatio, { style: cursorStyleForCurrentState(), pressed: false });
        scheduleNextFrame(getFrameIntervalMs());
        return;
    }

    if (!activeEnded) {
        return;
    }

    const finalRatio = state.cursorRatio;
    const mode = state.touchInteractionMode;
    clearInteractionState(false);

    if (mode === "drag" && finalRatio) {
        await sendPointerAction("up", finalRatio);
        elements.viewerStatus.textContent = "Drag finished.";
    } else if (mode === "scroll") {
        elements.viewerStatus.textContent = "Scroll finished.";
    } else {
        elements.viewerStatus.textContent = "Cursor moved.";
    }

    updateFrameCursor(finalRatio, { style: cursorStyleForCurrentState(), pressed: false });
    scheduleNextFrame(getFrameIntervalMs());
}

function handleFrameTouchCancel(event) {
    if (!state.selectedHandle || elements.windowFrame.hidden) {
        return;
    }

    if (state.touchMode === "direct") {
        handleDirectTouchCancel(event);
        return;
    }

    event.preventDefault();

    const shouldReleaseLeft = state.touchInteractionMode === "drag" && state.cursorRatio;
    const ratio = state.cursorRatio;
    clearInteractionState(false);
    updateFrameCursor(ratio, { style: cursorStyleForCurrentState(), pressed: false });

    if (shouldReleaseLeft) {
        sendPointerAction("up", ratio).catch(showTransientError);
    }

    scheduleNextFrame(getFrameIntervalMs());
}

async function activateTouchHoldMode() {
    const pendingId = state.touchPendingId;
    if (pendingId === null) {
        return;
    }

    clearPendingTouch();
    state.touchPendingId = null;
    state.touchPendingPoint = null;
    state.activePointerId = pendingId;
    state.touchInteractionMode = "drag";
    state.scrollCarry = 0;

    if (state.touchInteractionMode === "drag" && state.cursorRatio) {
        await sendPointerAction("down", state.cursorRatio);
        elements.viewerStatus.textContent = "Drag hold active.";
        updateFrameCursor(state.cursorRatio, { style: "mouse", pressed: true });
        return;
    }

    if (state.touchInteractionMode === "scroll") {
        elements.viewerStatus.textContent = "Scroll mode active.";
        updateFrameCursor(state.cursorRatio, { style: "scroll", pressed: false });
        return;
    }

    elements.viewerStatus.textContent = "Cursor moving.";
    updateFrameCursor(state.cursorRatio, { style: "mouse", pressed: false });
}

async function startOrUpdateTwoFingerScroll(touches) {
    const centerPoint = getTouchCenterPoint(touches);
    if (!centerPoint) {
        return;
    }

    const distance = getTouchDistance(touches);
    if (!distance) {
        return;
    }

    if (!state.touchTwoFingerScroll) {
        const shouldReleaseLeft = state.touchInteractionMode === "drag" && state.cursorRatio;
        flushPendingSingleTapNow();
        clearPendingTouch();
        state.touchPendingId = null;
        state.touchPendingPoint = null;

        if (shouldReleaseLeft) {
            await sendPointerAction("up", state.cursorRatio);
        }

        state.pointerActive = true;
        state.activeInputKind = "touch";
        state.activePointerId = null;
        state.touchInteractionMode = null;
        state.touchTwoFingerScroll = true;
        state.touchTwoFingerMode = null;
        state.touchLastCenterPoint = centerPoint;
        state.touchLastDistance = distance;
        state.pointerLastClientPoint = null;
        state.scrollCarry = 0;
        updateFrameCursor(state.cursorRatio, { style: cursorStyleForCurrentState(), pressed: false });
        elements.viewerStatus.textContent = "Two-finger gesture active.";
        return;
    }

    const previousCenterPoint = state.touchLastCenterPoint ?? centerPoint;
    const previousDistance = state.touchLastDistance ?? distance;
    const deltaX = centerPoint.x - previousCenterPoint.x;
    const deltaY = centerPoint.y - previousCenterPoint.y;
    const distanceDelta = distance - previousDistance;

    state.touchLastCenterPoint = centerPoint;
    state.touchLastDistance = distance;

    if (state.touchTwoFingerMode !== "zoom" && Math.abs(distanceDelta) >= PINCH_DISTANCE_THRESHOLD) {
        state.touchTwoFingerMode = "zoom";
    } else if (state.touchTwoFingerMode === null && Math.hypot(deltaX, deltaY) >= 1.5) {
        state.touchTwoFingerMode = "pan";
    }

    if (state.touchTwoFingerMode === "zoom") {
        applyPinchZoom(previousCenterPoint, centerPoint, previousDistance, distance);
        return;
    }

    state.touchTwoFingerMode = "pan";
    applyFramePan(previousCenterPoint, centerPoint);
}

function finishTwoFingerGesture() {
    state.touchTwoFingerScroll = false;
    state.touchTwoFingerMode = null;
    state.touchLastCenterPoint = null;
    state.touchLastDistance = null;
    state.pointerActive = false;
    state.activeInputKind = null;
    state.touchInteractionMode = null;
    state.pointerLastClientPoint = null;
    state.scrollCarry = 0;
    updateFrameCursor(state.cursorRatio, { style: cursorStyleForCurrentState(), pressed: false });
    elements.viewerStatus.textContent = `Zoom ${Math.round(state.frameScale * 100)}%.`;
    scheduleNextFrame(getFrameIntervalMs());
}

// --- Direct Touch Mode ---

function handleDirectTouchStart(event) {
    if (event.touches.length >= 2) {
        event.preventDefault();
        cancelDirectTouch();
        startDirectTwoFingerPan(event.touches);
        return;
    }

    if (event.touches.length !== 1) return;

    const touch = event.touches[0];
    event.preventDefault();

    const ratio = getFrameRatiosFromClient(touch.clientX, touch.clientY);
    if (!ratio) return;

    cancelDirectTouch();
    state.directTouchId = touch.identifier;
    state.directTouchStartPoint = pointFromTouch(touch);
    state.directTouchStartRatio = ratio;
    state.directTouchMode = null;
    state.pointerLastClientPoint = pointFromTouch(touch);
    state.cursorRatio = ratio;
    state.scrollCarry = 0;

    state.directTouchHoldTimer = window.setTimeout(() => {
        if (state.directTouchMode === null && state.directTouchId !== null) {
            state.directTouchMode = "longpress";
            sendPointerAction("move", state.directTouchStartRatio).catch(showTransientError);
            sendPointerAction("click", state.directTouchStartRatio, { button: "right" }).catch(showTransientError);
            elements.viewerStatus.textContent = "Right click sent.";
        }
    }, DIRECT_TAP_HOLD_MS);
}

function handleDirectTouchMove(event) {
    if (state.touchTwoFingerScroll && event.touches.length >= 2) {
        event.preventDefault();
        updateDirectTwoFingerPan(event.touches);
        return;
    }

    if (event.touches.length >= 2) {
        event.preventDefault();
        cancelDirectTouch();
        startDirectTwoFingerPan(event.touches);
        return;
    }

    const touch = findDirectTouch(event.touches);
    if (!touch) return;

    event.preventDefault();
    const point = pointFromTouch(touch);
    const prev = state.pointerLastClientPoint ?? point;
    state.pointerLastClientPoint = point;

    if (state.directTouchMode === null) {
        const dist = distanceBetween(point, state.directTouchStartPoint ?? point);
        if (dist < TOUCH_MOVE_THRESHOLD) return;
        // 移動開始 → スクロールモード
        state.directTouchMode = "scroll";
        if (state.directTouchHoldTimer) {
            clearTimeout(state.directTouchHoldTimer);
            state.directTouchHoldTimer = null;
        }
    }

    if (state.directTouchMode === "scroll") {
        const deltaY = prev.y - point.y;
        handleScrollDeltaPixels(deltaY * DIRECT_SCROLL_GAIN);
        return;
    }

    if (state.directTouchMode === "drag") {
        const ratio = getFrameRatiosFromClient(point.x, point.y);
        if (ratio) {
            queuePointerMove(ratio);
        }
    }
}

function handleDirectTouchEnd(event) {
    if (state.touchTwoFingerScroll) {
        event.preventDefault();
        if (event.touches.length < 2) {
            finishDirectTwoFingerPan();
        }
        return;
    }

    const ended = findDirectTouchInList(event.changedTouches);
    if (!ended) return;

    event.preventDefault();
    const mode = state.directTouchMode;
    const ratio = state.directTouchStartRatio;

    if (state.directTouchHoldTimer) {
        clearTimeout(state.directTouchHoldTimer);
        state.directTouchHoldTimer = null;
    }

    if (mode === "drag" && ratio) {
        const finalRatio = getFrameRatiosFromClient(ended.clientX, ended.clientY) || ratio;
        sendPointerAction("up", finalRatio).catch(showTransientError);
        elements.viewerStatus.textContent = "Drag finished.";
    } else if (mode === "longpress") {
        // 長押し右クリックは既に送信済み → 何もしない
    } else if (mode === "scroll") {
        elements.viewerStatus.textContent = "Scroll finished.";
    } else if (mode === null && ratio) {
        // タップ判定
        handleDirectTap(ratio);
    }

    clearDirectTouchState();
    scheduleNextFrame(getFrameIntervalMs());
}

function handleDirectTouchCancel(event) {
    event.preventDefault();
    if (state.directTouchMode === "drag" && state.directTouchStartRatio) {
        sendPointerAction("up", state.directTouchStartRatio).catch(showTransientError);
    }
    cancelDirectTouch();
    if (state.touchTwoFingerScroll) {
        finishDirectTwoFingerPan();
    }
    scheduleNextFrame(getFrameIntervalMs());
}

function handleDirectTap(ratio) {
    const now = Date.now();
    const prevRatio = state.directTouchTapRatio;
    const elapsed = now - state.directTouchTapTime;

    if (prevRatio && elapsed < DOUBLE_TAP_DELAY_MS) {
        const threshold = getDoubleTapDistanceThresholdRatio();
        if (distanceBetween(prevRatio, ratio) <= threshold) {
            // ダブルタップ → ダブルクリック
            state.directTouchTapTime = 0;
            state.directTouchTapRatio = null;
            sendPointerAction("move", ratio).catch(showTransientError);
            sendPointerAction("click", ratio, { button: "left" }).catch(showTransientError);
            sendPointerAction("click", ratio, { button: "left" }).catch(showTransientError);
            elements.viewerStatus.textContent = "Double click sent.";
            return;
        }
    }

    // シングルタップ → 左クリック
    state.directTouchTapTime = now;
    state.directTouchTapRatio = ratio;
    sendPointerAction("move", ratio).catch(showTransientError);
    sendPointerAction("click", ratio, { button: "left" }).catch(showTransientError);
    elements.viewerStatus.textContent = "Click sent.";
}

function cancelDirectTouch() {
    if (state.directTouchHoldTimer) {
        clearTimeout(state.directTouchHoldTimer);
        state.directTouchHoldTimer = null;
    }
    clearDirectTouchState();
}

function clearDirectTouchState() {
    state.directTouchId = null;
    state.directTouchStartPoint = null;
    state.directTouchStartRatio = null;
    state.directTouchMode = null;
    state.pointerLastClientPoint = null;
    state.scrollCarry = 0;
}

function findDirectTouch(touches) {
    if (state.directTouchId === null) return null;
    for (let i = 0; i < touches.length; i++) {
        if (touches[i].identifier === state.directTouchId) return touches[i];
    }
    return null;
}

function findDirectTouchInList(touchList) {
    if (state.directTouchId === null) return null;
    for (let i = 0; i < touchList.length; i++) {
        if (touchList[i].identifier === state.directTouchId) return touchList[i];
    }
    return null;
}

// --- Direct Touch: Two-finger pan (no constrain, free pan) ---

function startDirectTwoFingerPan(touches) {
    cancelDirectTouch();
    state.touchTwoFingerScroll = true;
    state.touchTwoFingerMode = "pan";
    state.touchLastCenterPoint = getTouchCenterPoint(touches);
    state.touchLastDistance = getTouchDistance(touches);
}

function updateDirectTwoFingerPan(touches) {
    const centerPoint = getTouchCenterPoint(touches);
    const distance = getTouchDistance(touches);
    if (!centerPoint || !distance) return;

    const prev = state.touchLastCenterPoint ?? centerPoint;
    const prevDist = state.touchLastDistance ?? distance;
    state.touchLastCenterPoint = centerPoint;
    state.touchLastDistance = distance;

    // ピンチズーム判定
    if (state.touchTwoFingerMode !== "zoom" && Math.abs(distance - prevDist) >= PINCH_DISTANCE_THRESHOLD) {
        state.touchTwoFingerMode = "zoom";
    }

    if (state.touchTwoFingerMode === "zoom") {
        applyPinchZoom(prev, centerPoint, prevDist, distance);
        return;
    }

    // 制約なしパン
    state.frameTranslateX += (centerPoint.x - prev.x);
    state.frameTranslateY += (centerPoint.y - prev.y);
    syncFrameTransform();
}

function finishDirectTwoFingerPan() {
    state.touchTwoFingerScroll = false;
    state.touchTwoFingerMode = null;
    state.touchLastCenterPoint = null;
    state.touchLastDistance = null;
    elements.viewerStatus.textContent = `Zoom ${Math.round(state.frameScale * 100)}%.`;
    scheduleNextFrame(getFrameIntervalMs());
}

function handleScrollGesture(previousRatio, ratio) {
    const contentRect = getFrameContentRect();
    if (!contentRect) {
        return;
    }

    const deltaPixels = (ratio.y - previousRatio.y) * contentRect.height;
    handleScrollDeltaPixels(deltaPixels);
}

function handleScrollDeltaPixels(deltaPixels) {
    if (!state.cursorRatio) {
        return;
    }

    state.scrollCarry += deltaPixels;

    while (Math.abs(state.scrollCarry) >= WHEEL_STEP_PIXELS) {
        const wheelDelta = state.scrollCarry > 0 ? -120 : 120;
        state.scrollCarry += state.scrollCarry > 0 ? -WHEEL_STEP_PIXELS : WHEEL_STEP_PIXELS;
        sendPointerAction("wheel", state.cursorRatio, { wheelDelta }).catch(showTransientError);
    }
}

function handleScrollPadPointerDown(event, padElement) {
    if (!state.selectedHandle) {
        return;
    }

    if (state.scrollPadActive && state.scrollPadPointerId !== null && state.scrollPadPointerId !== event.pointerId) {
        finishScrollPadInteraction(state.scrollPadPointerId, false);
    }

    event.preventDefault();
    state.scrollPadActive = true;
    state.scrollPadPointerId = event.pointerId;
    state.scrollPadLastClientY = event.clientY;
    state.scrollPadCarry = 0;
    state.scrollPadElement = padElement || elements.scrollPad;
    state.scrollPadElement.classList.add("active");

    if (typeof state.scrollPadElement.setPointerCapture === "function") {
        state.scrollPadElement.setPointerCapture(event.pointerId);
    }
}

function handleScrollPadPointerMove(event) {
    if (!state.scrollPadActive || state.scrollPadPointerId !== event.pointerId) {
        return;
    }

    event.preventDefault();
    const previousY = state.scrollPadLastClientY ?? event.clientY;
    const deltaY = previousY - event.clientY;
    state.scrollPadLastClientY = event.clientY;
    handleScrollPadDeltaPixels(deltaY);
}

function handleScrollPadPointerUp(event) {
    if (state.scrollPadPointerId !== event.pointerId) {
        return;
    }

    event.preventDefault();
    finishScrollPadInteraction(event.pointerId);
}

function handleScrollPadPointerCancel(event) {
    if (state.scrollPadPointerId !== event.pointerId) {
        return;
    }

    finishScrollPadInteraction(event.pointerId);
}

function finishScrollPadInteraction(pointerId, shouldSchedule = true) {
    var activeEl = state.scrollPadElement || elements.scrollPad;
    if (pointerId !== null && typeof activeEl.releasePointerCapture === "function") {
        try {
            activeEl.releasePointerCapture(pointerId);
        } catch {
        }
    }

    state.scrollPadActive = false;
    state.scrollPadPointerId = null;
    state.scrollPadLastClientY = null;
    state.scrollPadCarry = 0;
    state.scrollPadElement = null;
    activeEl.classList.remove("active");
    if (shouldSchedule) {
        scheduleNextFrame(getFrameIntervalMs());
    }
}

function handleScrollPadDeltaPixels(deltaPixels) {
    const ratio = state.cursorRatio ?? { x: 0.5, y: 0.5 };
    state.scrollPadCarry += deltaPixels;

    while (Math.abs(state.scrollPadCarry) >= WHEEL_STEP_PIXELS) {
        const wheelDelta = state.scrollPadCarry > 0 ? -120 : 120;
        state.scrollPadCarry += state.scrollPadCarry > 0 ? -WHEEL_STEP_PIXELS : WHEEL_STEP_PIXELS;
        sendPointerAction("wheel", ratio, { wheelDelta }).catch(showTransientError);
    }
}

function applyPinchZoom(previousCenterPoint, centerPoint, previousDistance, distance) {
    const stageRect = elements.frameStage.getBoundingClientRect();
    if (!stageRect.width || !stageRect.height || previousDistance <= 0 || distance <= 0) {
        return;
    }

    const oldScale = state.frameScale;
    const nextScale = clamp(oldScale * (distance / previousDistance), FRAME_SCALE_MIN, FRAME_SCALE_MAX);
    const previousLocalCenter = {
        x: previousCenterPoint.x - stageRect.left,
        y: previousCenterPoint.y - stageRect.top,
    };
    const currentLocalCenter = {
        x: centerPoint.x - stageRect.left,
        y: centerPoint.y - stageRect.top,
    };

    let nextTranslateX = state.frameTranslateX + (currentLocalCenter.x - previousLocalCenter.x);
    let nextTranslateY = state.frameTranslateY + (currentLocalCenter.y - previousLocalCenter.y);

    if (nextScale !== oldScale) {
        const stageCenterX = stageRect.width / 2;
        const stageCenterY = stageRect.height / 2;
        const scaleRatio = nextScale / oldScale;

        nextTranslateX = currentLocalCenter.x - stageCenterX - ((currentLocalCenter.x - stageCenterX - nextTranslateX) * scaleRatio);
        nextTranslateY = currentLocalCenter.y - stageCenterY - ((currentLocalCenter.y - stageCenterY - nextTranslateY) * scaleRatio);
    }

    state.frameScale = nextScale;
    state.frameTranslateX = nextTranslateX;
    state.frameTranslateY = nextTranslateY;
    constrainFrameTransform();
    syncFrameTransform();
    syncFrameCursor();
    elements.viewerStatus.textContent = `Zoom ${Math.round(state.frameScale * 100)}%.`;
}

function applyFramePan(previousCenterPoint, centerPoint) {
    state.frameTranslateX += (centerPoint.x - previousCenterPoint.x);
    state.frameTranslateY += (centerPoint.y - previousCenterPoint.y);
    constrainFrameTransform();
    syncFrameTransform();
    syncFrameCursor();
}

function applyRelativeCursorDelta(deltaX, deltaY, options = {}) {
    const stageRect = elements.frameStage.getBoundingClientRect();
    if (!stageRect.width || !stageRect.height) {
        return;
    }

    const current = state.cursorRatio ?? { x: 0.5, y: 0.5 };
    const nextRatio = {
        x: clamp(current.x + (deltaX / stageRect.width) * CURSOR_SPEED, 0, 1),
        y: clamp(current.y + (deltaY / stageRect.height) * CURSOR_SPEED, 0, 1),
    };

    const changed = !state.cursorRatio
        || Math.abs(nextRatio.x - current.x) >= 0.0005
        || Math.abs(nextRatio.y - current.y) >= 0.0005;

    updateFrameCursor(nextRatio, {
        style: options.style ?? cursorStyleForCurrentState(),
        pressed: Boolean(options.pressed),
    });

    if (changed && options.sendMove !== false) {
        queuePointerMove(nextRatio);
    }
}

function syncFrameTransform() {
    const translateX = Math.round(state.frameTranslateX * 100) / 100;
    const translateY = Math.round(state.frameTranslateY * 100) / 100;
    const scale = Math.round(getEffectiveFrameScale() * 1000) / 1000;
    elements.windowFrame.style.transform = `translate(${translateX}px, ${translateY}px) scale(${scale})`;
}

function getEffectiveFrameScale() {
    return state.frameScale;
}

function updateAutoFrameScale() {
    state.frameAutoScale = 1;
    constrainFrameTransform();
}

function computeAutoFrameScale() {
    return 1;
}

function constrainFrameTransform() {
    if (!elements.frameStage) {
        return;
    }

    if (state.touchMode === "direct") {
        return;
    }

    const stageRect = elements.frameStage.getBoundingClientRect();
    const effectiveScale = getEffectiveFrameScale();
    const maxOffsetX = Math.max(0, ((effectiveScale - 1) * stageRect.width) / 2);
    const maxOffsetY = Math.max(0, ((effectiveScale - 1) * stageRect.height) / 2);
    state.frameTranslateX = clamp(state.frameTranslateX, -maxOffsetX, maxOffsetX);
    state.frameTranslateY = clamp(state.frameTranslateY, -maxOffsetY, maxOffsetY);

    if (state.frameScale <= FRAME_SCALE_MIN + 0.001) {
        state.frameScale = FRAME_SCALE_MIN;
        state.frameTranslateX = 0;
        state.frameTranslateY = 0;
    }
}

function queuePointerMove(ratio) {
    state.queuedMoveRatio = ratio;
    if (state.moveRequestInFlight) {
        return;
    }

    flushPointerMoveQueue().catch(showTransientError);
}

async function flushPointerMoveQueue() {
    if (!state.queuedMoveRatio) {
        return;
    }

    state.moveRequestInFlight = true;
    try {
        while (state.queuedMoveRatio) {
            const ratio = state.queuedMoveRatio;
            state.queuedMoveRatio = null;
            await sendPointerAction("move", ratio);
        }
    } finally {
        state.moveRequestInFlight = false;
    }
}

function updateFrameCursor(ratio, options = {}) {
    if (!ratio || !state.selectedWindow || elements.windowFrame.hidden || state.touchMode === "direct") {
        elements.frameCursor.hidden = true;
        return;
    }

    state.cursorRatio = {
        x: clamp(ratio.x, 0, 1),
        y: clamp(ratio.y, 0, 1),
    };
    state.cursorStyle = options.style ?? state.cursorStyle ?? "mouse";

    elements.frameCursor.classList.toggle("pressed", Boolean(options.pressed));
    elements.frameCursor.classList.toggle("touch-cursor", state.cursorStyle === "touch");
    elements.frameCursor.classList.toggle("scroll-cursor", state.cursorStyle === "scroll");
    syncFrameCursor();
}

function handleFrameMetadataLoaded() {
    state.frameVideoWidth = elements.windowFrame.videoWidth || state.frameVideoWidth || 0;
    state.frameVideoHeight = elements.windowFrame.videoHeight || state.frameVideoHeight || 0;
    updateAutoFrameScale();
    syncFrameTransform();
    syncFrameCursor();
}

function syncFrameCursor() {
    if (!state.cursorRatio || !state.selectedWindow || elements.windowFrame.hidden || state.touchMode === "direct") {
        elements.frameCursor.hidden = true;
        return;
    }

    const contentRect = getFrameContentRect();
    if (!contentRect) {
        elements.frameCursor.hidden = true;
        return;
    }

    const stageRect = elements.frameStage.getBoundingClientRect();
    const left = contentRect.left - stageRect.left + (state.cursorRatio.x * contentRect.width);
    const top = contentRect.top - stageRect.top + (state.cursorRatio.y * contentRect.height);

    elements.frameCursor.hidden = false;
    elements.frameCursor.style.left = `${left}px`;
    elements.frameCursor.style.top = `${top}px`;
}

function getFrameContentRect() {
    const rect = elements.windowFrame.getBoundingClientRect();
    if (!rect.width || !rect.height) {
        return null;
    }

    const encodedWidth = state.frameVideoWidth || elements.windowFrame.videoWidth || state.selectedWindow?.bounds.width || rect.width;
    const encodedHeight = state.frameVideoHeight || elements.windowFrame.videoHeight || state.selectedWindow?.bounds.height || rect.height;
    const sourceWidth = state.selectedWindow?.bounds.width || encodedWidth;
    const sourceHeight = state.selectedWindow?.bounds.height || encodedHeight;

    const encodedRect = fitRectContain(rect, encodedWidth, encodedHeight);
    if (!encodedRect) {
        return rect;
    }

    const sourceRect = fitRectContain(encodedRect, sourceWidth, sourceHeight);
    return sourceRect || encodedRect;
}

function fitRectContain(containerRect, contentWidth, contentHeight) {
    if (!containerRect?.width || !containerRect?.height || !contentWidth || !contentHeight) {
        return null;
    }

    const contentAspect = contentWidth / contentHeight;
    const containerAspect = containerRect.width / containerRect.height;
    if (!Number.isFinite(contentAspect) || !Number.isFinite(containerAspect) || contentAspect <= 0 || containerAspect <= 0) {
        return null;
    }

    if (contentAspect > containerAspect) {
        const height = containerRect.width / contentAspect;
        return {
            left: containerRect.left,
            top: containerRect.top + ((containerRect.height - height) / 2),
            width: containerRect.width,
            height,
        };
    }

    const width = containerRect.height * contentAspect;
    return {
        left: containerRect.left + ((containerRect.width - width) / 2),
        top: containerRect.top,
        width,
        height: containerRect.height,
    };
}

function getFrameRatiosFromClient(clientX, clientY) {
    const rect = getFrameContentRect();
    if (!rect || !rect.width || !rect.height) {
        return null;
    }

    const x = (clientX - rect.left) / rect.width;
    const y = (clientY - rect.top) / rect.height;
    if (x < 0 || x > 1 || y < 0 || y > 1) {
        return null;
    }

    return { x, y };
}

function clearInteractionState(clearCursorStyle = true) {
    clearPendingTouch();
    clearPendingSingleTap(false);
    state.pointerActive = false;
    state.activePointerId = null;
    state.activeInputKind = null;
    state.touchInteractionMode = null;
    state.pointerLastRatio = null;
    state.pointerLastClientPoint = null;
    state.touchTwoFingerScroll = false;
    state.touchTwoFingerMode = null;
    state.touchLastCenterPoint = null;
    state.touchLastDistance = null;
    state.touchPendingMoved = false;
    state.touchPendingId = null;
    state.touchPendingPoint = null;
    state.scrollCarry = 0;
    state.queuedMoveRatio = null;
    if (clearCursorStyle) {
        state.cursorStyle = "mouse";
    }
}

function clearPendingTouch() {
    if (state.touchPendingTimer) {
        window.clearTimeout(state.touchPendingTimer);
        state.touchPendingTimer = null;
    }
}

function handleLeftTap(ratio) {
    const tapIntent = resolveTapIntent(ratio);
    if (tapIntent === "double") {
        // ダブルタップ → ダブルクリック
        sendPointerAction("click", ratio, { button: "left" }).catch(showTransientError);
        sendPointerAction("click", ratio, { button: "left" }).catch(showTransientError);
        elements.viewerStatus.textContent = "Double click sent.";
        return;
    }

    if (tapIntent === "flush-first") {
        flushPendingSingleTapNow();
    }

    queuePendingSingleTap(ratio);
    elements.viewerStatus.textContent = "Left click pending.";
}

function queuePendingSingleTap(ratio) {
    clearPendingSingleTap(false);
    state.pendingTapRatio = ratio;
    state.pendingTapTime = Date.now();
    state.pendingTapTimer = window.setTimeout(() => {
        flushPendingSingleTapNow();
    }, DOUBLE_TAP_DELAY_MS);
}

function resolveTapIntent(ratio) {
    if (!state.pendingTapRatio || !state.pendingTapTime) {
        return "single";
    }

    const elapsed = Date.now() - state.pendingTapTime;
    if (elapsed > DOUBLE_TAP_DELAY_MS) {
        clearPendingSingleTap(false);
        return "single";
    }

    const threshold = getDoubleTapDistanceThresholdRatio();
    const matches = distanceBetween(state.pendingTapRatio, ratio) <= threshold;
    clearPendingSingleTap(false);
    return matches ? "double" : "flush-first";
}

function flushPendingSingleTapNow() {
    if (!state.pendingTapRatio) {
        clearPendingSingleTap(false);
        return;
    }

    const ratio = state.pendingTapRatio;
    clearPendingSingleTap(false);
    sendPointerAction("click", ratio, { button: "left" }).catch(showTransientError);
    elements.viewerStatus.textContent = "Left click sent.";
    scheduleNextFrame(getFrameIntervalMs());
}

function clearPendingSingleTap(resetStatus = true) {
    if (state.pendingTapTimer) {
        window.clearTimeout(state.pendingTapTimer);
        state.pendingTapTimer = null;
    }

    state.pendingTapRatio = null;
    state.pendingTapTime = 0;

    if (resetStatus && !elements.workspace.hidden) {
        elements.viewerStatus.textContent = "";
    }
}

function findTrackedTouch(touchList) {
    if (state.touchPendingId !== null) {
        const pendingTouch = findTouchById(touchList, state.touchPendingId);
        if (pendingTouch) {
            return pendingTouch;
        }
    }

    if (state.activePointerId !== null) {
        return findTouchById(touchList, state.activePointerId);
    }

    return touchList.length > 0 ? touchList[0] : null;
}

function findTouchById(touchList, identifier) {
    if (identifier === null || identifier === undefined) {
        return null;
    }

    for (const touch of touchList) {
        if (touch.identifier === identifier) {
            return touch;
        }
    }

    return null;
}

function touchListContains(touchList, identifier) {
    return Boolean(findTouchById(touchList, identifier));
}

function pointFromTouch(touch) {
    return { x: touch.clientX, y: touch.clientY };
}

function getTouchCenterPoint(touches) {
    if (!touches || touches.length < 2) {
        return null;
    }

    let x = 0;
    let y = 0;
    for (const touch of touches) {
        x += touch.clientX;
        y += touch.clientY;
    }

    return {
        x: x / touches.length,
        y: y / touches.length,
    };
}

function getTouchDistance(touches) {
    if (!touches || touches.length < 2) {
        return null;
    }

    return distanceBetween(
        { x: touches[0].clientX, y: touches[0].clientY },
        { x: touches[1].clientX, y: touches[1].clientY },
    );
}

function getDoubleTapDistanceThresholdRatio() {
    const contentRect = getFrameContentRect();
    if (!contentRect) {
        return 0.04;
    }

    return DOUBLE_TAP_MAX_DISTANCE_PX / Math.max(contentRect.width, contentRect.height, 1);
}

function distanceBetween(a, b) {
    return Math.hypot((a.x - b.x), (a.y - b.y));
}

function cursorStyleForCurrentState() {
    return "mouse";
}

function isPressedCursor() {
    return state.touchInteractionMode === "drag" || (state.activeInputKind !== null && state.activeInputKind !== "touch");
}

async function sendPointerAction(action, ratio, options = {}) {
    if (!state.selectedHandle || !ratio) {
        return;
    }

    const response = await fetch(`/api/windows/${state.selectedHandle}/input/pointer`, {
        method: "POST",
        credentials: "same-origin",
        headers: {
            "Content-Type": "application/json",
        },
        body: JSON.stringify({
            action,
            xRatio: ratio.x,
            yRatio: ratio.y,
            button: options.button || "left",
            clicks: options.clicks,
            wheelDelta: options.wheelDelta,
        }),
    });

    if (!response.ok) {
        throw new Error(await readErrorMessage(response));
    }
}

async function handleSendText(event) {
    event.preventDefault();

    if (!state.selectedHandle) {
        return;
    }

    const text = elements.textInput.value;
    if (!text.trim()) {
        await fetch(`/api/windows/${state.selectedHandle}/input/key`, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ key: "Enter" }),
        });
        return;
    }

    const response = await fetch(`/api/windows/${state.selectedHandle}/input/text`, {
        method: "POST",
        credentials: "same-origin",
        headers: {
            "Content-Type": "application/json",
        },
        body: JSON.stringify({ text }),
    });

    if (!response.ok) {
        throw new Error(await readErrorMessage(response));
    }

    elements.textInput.value = "";
    elements.viewerStatus.textContent = "Text sent.";
    scheduleNextFrame(getFrameIntervalMs());
}

function sendKey(key) {
    if (!state.selectedHandle) {
        return;
    }

    fetch(`/api/windows/${state.selectedHandle}/input/key`, {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ key }),
    }).then((response) => {
        if (response.ok) {
            elements.viewerStatus.textContent = `${key} sent.`;
        }
    }).catch(() => {});
}

// --- Custom Keys ---

function loadCustomKeys() {
    try {
        const raw = window.localStorage.getItem(CUSTOM_KEYS_STORAGE_KEY);
        const keys = raw ? JSON.parse(raw) : [];
        renderCustomKeys(keys);
    } catch {
        renderCustomKeys([]);
    }
}

function saveCustomKeys(keys) {
    try {
        window.localStorage.setItem(CUSTOM_KEYS_STORAGE_KEY, JSON.stringify(keys));
    } catch {}
}

function getCustomKeys() {
    try {
        const raw = window.localStorage.getItem(CUSTOM_KEYS_STORAGE_KEY);
        return raw ? JSON.parse(raw) : [];
    } catch {
        return [];
    }
}

const KEY_OPTIONS = [
    "A","B","C","D","E","F","G","H","I","J","K","L","M",
    "N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
    "0","1","2","3","4","5","6","7","8","9",
    "F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12",
    "Enter","Space","Escape","Tab","Backspace","Delete","Insert",
    "Home","End","PageUp","PageDown",
    "Up","Down","Left","Right",
    "PrintScreen","ScrollLock","Pause",
    "NumLock","NumPad0","NumPad1","NumPad2","NumPad3","NumPad4",
    "NumPad5","NumPad6","NumPad7","NumPad8","NumPad9",
    "Multiply","Add","Subtract","Decimal","Divide",
    ";","=",",","-",".","/","`","[","\\","]","'",
];

function showKeyDropdown(filter) {
    const query = filter.toLowerCase();
    const matches = query
        ? KEY_OPTIONS.filter(k => k.toLowerCase().includes(query))
        : KEY_OPTIONS;

    const dropdown = elements.keyDropdown;
    dropdown.innerHTML = "";

    if (matches.length === 0) {
        dropdown.hidden = true;
        return;
    }

    for (const key of matches.slice(0, 30)) {
        const item = document.createElement("div");
        item.className = "key-dropdown-item";
        item.textContent = key;
        item.addEventListener("pointerdown", (e) => {
            e.preventDefault();
            elements.customKeyInput.value = key;
            dropdown.hidden = true;
        });
        dropdown.appendChild(item);
    }
    dropdown.hidden = false;
}

function hideKeyDropdown() {
    elements.keyDropdown.hidden = true;
}

function addCustomKey() {
    const keyName = elements.customKeyInput.value.trim();
    if (!keyName) return;

    const modifiers = [];
    if (elements.modCtrl.classList.contains("active")) modifiers.push("Ctrl");
    if (elements.modShift.classList.contains("active")) modifiers.push("Shift");
    if (elements.modAlt.classList.contains("active")) modifiers.push("Alt");
    if (elements.modWin.classList.contains("active")) modifiers.push("Win");

    const combo = [...modifiers, keyName].join("+");
    const label = elements.customKeyLabel.value.trim() || combo;

    const keys = getCustomKeys();
    keys.push({ combo, label });
    saveCustomKeys(keys);
    renderCustomKeys(keys);

    elements.customKeyInput.value = "";
    elements.customKeyLabel.value = "";
    elements.modCtrl.classList.remove("active");
    elements.modShift.classList.remove("active");
    elements.modAlt.classList.remove("active");
    elements.modWin.classList.remove("active");
}

function removeCustomKey(index) {
    const keys = getCustomKeys();
    keys.splice(index, 1);
    saveCustomKeys(keys);
    renderCustomKeys(keys);
}

function renderCustomKeys(keys) {
    const container = elements.customKeyList;
    container.innerHTML = "";
    keys.forEach((entry, index) => {
        const chip = document.createElement("div");
        chip.className = "custom-key-chip";

        const label = document.createElement("span");
        label.textContent = entry.label;
        label.addEventListener("click", () => sendKey(entry.combo));
        chip.appendChild(label);

        const del = document.createElement("span");
        del.className = "chip-delete";
        del.textContent = "\u00d7";
        del.addEventListener("click", (e) => {
            e.stopPropagation();
            removeCustomKey(index);
        });
        chip.appendChild(del);

        container.appendChild(chip);
    });
}

function handleGlobalKeyDown(event) {
    if (event.key === "Escape" && state.drawerOpen) {
        closeDrawer();
    }
}

function preventBrowserZoom(event) {
    event.preventDefault();
}

let _lastTouchEndTime = 0;
function preventDoubleTapZoom(event) {
    const now = Date.now();
    if (now - _lastTouchEndTime <= 300) {
        event.preventDefault();
    }
    _lastTouchEndTime = now;
}

function toggleDrawer() {
    if (state.drawerOpen) {
        closeDrawer();
        return;
    }

    openDrawer();
}

function openDrawer() {
    state.drawerOpen = true;
    syncDrawerState();
}

function closeDrawer(immediate = false) {
    state.drawerOpen = false;
    syncDrawerState(immediate);
}

function syncDrawerState(immediate = false) {
    elements.workspace.classList.toggle("drawer-open", state.drawerOpen);
    elements.sideDrawer.setAttribute("aria-hidden", state.drawerOpen ? "false" : "true");
    if (immediate && !state.drawerOpen) {
        elements.workspace.classList.remove("drawer-open");
    }
}

function wait(milliseconds) {
    return new Promise((resolve) => window.setTimeout(resolve, Math.max(0, milliseconds)));
}

async function waitForIceGatheringComplete(peerConnection) {
    if (!peerConnection || peerConnection.iceGatheringState === "complete") {
        return;
    }

    await new Promise((resolve) => {
        const timeout = window.setTimeout(() => {
            cleanup();
            resolve();
        }, 2000);

        const handleStateChange = () => {
            if (peerConnection.iceGatheringState === "complete") {
                cleanup();
                resolve();
            }
        };

        const cleanup = () => {
            window.clearTimeout(timeout);
            peerConnection.removeEventListener("icegatheringstatechange", handleStateChange);
        };

        peerConnection.addEventListener("icegatheringstatechange", handleStateChange);
    });
}

function normalizeError(error, fallbackMessage) {
    if (error instanceof Error) {
        return error;
    }

    return new Error(fallbackMessage);
}

function showTransientError(error) {
    if (elements.workspace.hidden) {
        elements.loginError.hidden = false;
        elements.loginError.textContent = error.message;
        return;
    }

    elements.viewerStatus.textContent = error.message;
}

function showFatalError(error) {
    if (elements.workspace.hidden) {
        elements.loginError.hidden = false;
        elements.loginError.textContent = error.message;
        return;
    }

    elements.viewerStatus.textContent = error.message;
}

async function readErrorMessage(response) {
    try {
        const payload = await response.json();
        return payload.message || `Request failed with ${response.status}`;
    } catch {
        return `Request failed with ${response.status}`;
    }
}

function escapeHtml(value) {
    return value
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll("\"", "&quot;")
        .replaceAll("'", "&#39;");
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}
