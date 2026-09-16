// xterm.js interop for interactive Copilot agent test harness
window.xtermInterop = {
    instances: {},

    async init(container, dotNetRef, hubUrl = '/hubs/terminal') {
        if (!container) return;

        // Clean up previous instance if attached to this element
        const elementId = container.id || ('term-' + Math.random().toString(36).substr(2, 9));
        container.id = elementId;

        if (this.instances[elementId]) {
            this.dispose(elementId);
        }

        // Initialize xterm.js
        const term = new Terminal({
            cursorBlink: true,
            cursorStyle: 'block',
            fontSize: 14,
            fontFamily: 'Menlo, Monaco, "Courier New", monospace',
            theme: {
                background: '#121317',
                foreground: '#f1f1f1',
                cursor: '#58a6ff',
                selectionBackground: 'rgba(88, 166, 255, 0.3)',
                black: '#000000',
                red: '#ff7b72',
                green: '#3fb950',
                yellow: '#d29922',
                blue: '#58a6ff',
                magenta: '#bc8cff',
                cyan: '#39c5cf',
                white: '#b1bac4',
                brightBlack: '#6e7681',
                brightRed: '#ffa198',
                brightGreen: '#56d364',
                brightYellow: '#e3b341',
                brightBlue: '#79c0ff',
                brightMagenta: '#d2a8ff',
                brightCyan: '#56d4dd',
                brightWhite: '#ffffff'
            }
        });

        // Initialize FitAddon
        const fitAddon = new FitAddon.FitAddon();
        term.loadAddon(fitAddon);

        term.open(container);
        try {
            fitAddon.fit();
        } catch (e) {
            console.warn("Initial fit deferred:", e);
        }

        // Initialize SignalR connection
        const connection = new signalR.HubConnectionBuilder()
            .withUrl(hubUrl)
            .withAutomaticReconnect()
            .configureLogging(signalR.LogLevel.Information)
            .build();

        const state = {
            id: elementId,
            container: container,
            term: term,
            fitAddon: fitAddon,
            connection: connection,
            dotNetRef: dotNetRef,
            sessionId: null,
            resizeObserver: null
        };

        this.instances[elementId] = state;

        // Wire incoming SignalR messages
        connection.on("ReceiveOutput", (sessionId, data) => {
            if (state.sessionId && state.sessionId !== sessionId) return;
            term.write(data);
        });

        connection.on("SessionStateChanged", (sessionId, statusDto) => {
            if (state.sessionId && state.sessionId !== sessionId) return;
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync("OnSessionStateChanged", statusDto);
            }
        });

        connection.on("SessionFinished", (sessionId, exitCode) => {
            if (state.sessionId && state.sessionId !== sessionId) return;
            term.writeln(`\r\n\x1b[33m[Session terminated with exit code: ${exitCode}]\x1b[0m\r\n`);
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync("OnSessionFinished", sessionId, exitCode);
            }
        });

        // Wire keystrokes -> hub
        term.onData(data => {
            if (state.sessionId && connection.state === signalR.HubConnectionState.Connected) {
                connection.invoke("SendInput", state.sessionId, data).catch(err => {
                    console.error("Failed to send terminal input:", err);
                });
            }
        });

        // Wire resize -> hub
        term.onResize(size => {
            if (state.sessionId && connection.state === signalR.HubConnectionState.Connected) {
                connection.invoke("Resize", state.sessionId, size.cols, size.rows).catch(err => {
                    console.error("Failed to send terminal resize:", err);
                });
            }
        });

        // Window & Container resize handling
        const handleResize = () => {
            try {
                fitAddon.fit();
            } catch (e) { }
        };

        window.addEventListener("resize", handleResize);
        state.windowResizeHandler = handleResize;

        if (window.ResizeObserver) {
            state.resizeObserver = new ResizeObserver(() => {
                try {
                    fitAddon.fit();
                } catch (e) { }
            });
            state.resizeObserver.observe(container);
        }

        // Start SignalR connection
        try {
            await connection.start();
            console.log("TerminalHub SignalR connected successfully");
        } catch (err) {
            term.writeln(`\x1b[31mFailed to connect to TerminalHub: ${err.message}\x1b[0m`);
            console.error("SignalR connection error:", err);
        }

        return elementId;
    },

    async startSession(elementId, targetDir, requirements) {
        const state = this.instances[elementId];
        if (!state) return null;

        state.term.clear();
        state.term.writeln("\x1b[36m⚡ Launching Copilot Test Agent session...\x1b[0m\r\n");

        try {
            const cols = state.term.cols || 80;
            const rows = state.term.rows || 24;
            const sessionId = await state.connection.invoke("StartSession", targetDir, requirements, cols, rows);
            state.sessionId = sessionId;
            state.term.focus();
            return sessionId;
        } catch (err) {
            state.term.writeln(`\r\n\x1b[31mError starting session: ${err.message}\x1b[0m\r\n`);
            throw err;
        }
    },

    async startShell(elementId, targetDir) {
        const state = this.instances[elementId];
        if (!state) return null;

        state.term.clear();
        state.term.writeln("\x1b[32m⚡ Starting interactive POSIX shell smoke test...\x1b[0m\r\n");

        try {
            const cols = state.term.cols || 80;
            const rows = state.term.rows || 24;
            const sessionId = await state.connection.invoke("StartShellSession", targetDir || "", cols, rows);
            state.sessionId = sessionId;
            state.term.focus();
            return sessionId;
        } catch (err) {
            state.term.writeln(`\r\n\x1b[31mError starting shell: ${err.message}\x1b[0m\r\n`);
            throw err;
        }
    },

    async stopSession(elementId) {
        const state = this.instances[elementId];
        if (!state || !state.sessionId) return;

        try {
            await state.connection.invoke("StopSession", state.sessionId);
        } catch (err) {
            console.error("Error stopping session:", err);
        }
    },

    async interruptSession(elementId) {
        const state = this.instances[elementId];
        if (!state || !state.sessionId) return;

        try {
            await state.connection.invoke("InterruptSession", state.sessionId);
        } catch (err) {
            console.error("Error interrupting session:", err);
        }
    },

    clear(elementId) {
        const state = this.instances[elementId];
        if (state) {
            state.term.clear();
        }
    },

    focus(elementId) {
        const state = this.instances[elementId];
        if (state) {
            state.term.focus();
        }
    },

    dispose(elementId) {
        const state = this.instances[elementId];
        if (!state) return;

        if (state.windowResizeHandler) {
            window.removeEventListener("resize", state.windowResizeHandler);
        }
        if (state.resizeObserver) {
            state.resizeObserver.disconnect();
        }
        if (state.connection) {
            state.connection.stop().catch(() => { });
        }
        if (state.term) {
            state.term.dispose();
        }
        delete this.instances[elementId];
    }
};
