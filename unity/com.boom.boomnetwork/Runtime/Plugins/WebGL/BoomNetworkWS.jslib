var BoomNetworkWSLib = {

    $wsState: {
        sockets: {},
        nextId: 1
    },

    BoomNetworkWS_Connect: function (urlPtr) {
        var url = UTF8ToString(urlPtr);
        var id = wsState.nextId++;
        var entry = {
            ws: null,
            recvQueue: [],
            state: 1, // Connecting
            error: ""
        };
        wsState.sockets[id] = entry;

        try {
            var ws = new WebSocket(url);
            ws.binaryType = "arraybuffer";

            ws.onopen = function () {
                entry.state = 2; // Connected
            };

            ws.onmessage = function (evt) {
                if (evt.data instanceof ArrayBuffer) {
                    entry.recvQueue.push(new Uint8Array(evt.data));
                }
            };

            ws.onerror = function () {
                entry.error = "WebSocket error";
            };

            ws.onclose = function (evt) {
                entry.state = 0; // Disconnected
                if (!entry.error && evt.code !== 1000) {
                    entry.error = "closed: " + evt.code + " " + (evt.reason || "");
                }
            };

            entry.ws = ws;
        } catch (e) {
            entry.state = 0;
            entry.error = e.toString();
        }

        return id;
    },

    BoomNetworkWS_GetState: function (id) {
        var entry = wsState.sockets[id];
        if (!entry) return 0;
        return entry.state;
    },

    BoomNetworkWS_Send: function (id, bufPtr, len) {
        var entry = wsState.sockets[id];
        if (!entry || !entry.ws || entry.state !== 2) return -1;

        try {
            var data = HEAPU8.subarray(bufPtr, bufPtr + len);
            // Must copy — subarray is a view into Emscripten heap that may move
            var copy = new Uint8Array(len);
            copy.set(data);
            entry.ws.send(copy.buffer);
            return 0;
        } catch (e) {
            entry.error = e.toString();
            return -1;
        }
    },

    BoomNetworkWS_Poll: function (id, outBufPtr, maxLen) {
        var entry = wsState.sockets[id];
        if (!entry) return 0;

        if (entry.recvQueue.length === 0) return 0;

        var msg = entry.recvQueue.shift();
        var copyLen = Math.min(msg.length, maxLen);
        HEAPU8.set(msg.subarray(0, copyLen), outBufPtr);
        return copyLen;
    },

    BoomNetworkWS_GetError: function (id, outBufPtr, maxLen) {
        var entry = wsState.sockets[id];
        if (!entry || !entry.error) return 0;

        var err = entry.error;
        entry.error = "";
        var bytes = lengthBytesUTF8(err);
        if (bytes >= maxLen) bytes = maxLen - 1;
        stringToUTF8(err, outBufPtr, bytes + 1);
        return bytes;
    },

    BoomNetworkWS_Close: function (id) {
        var entry = wsState.sockets[id];
        if (!entry) return;

        if (entry.ws) {
            try {
                if (entry.ws.readyState === 0 || entry.ws.readyState === 1) {
                    entry.ws.close(1000, "");
                }
            } catch (e) { }
            entry.ws = null;
        }
        entry.state = 0;
        entry.recvQueue = [];
    }
};

autoAddDeps(BoomNetworkWSLib, '$wsState');
mergeInto(LibraryManager.library, BoomNetworkWSLib);
