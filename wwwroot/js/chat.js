window.simpleChatScroll = {
    scrollToBottom: function (el) {
        if (!el) return;
        try {
            el.scrollTop = el.scrollHeight;
            const parent = el.parentElement;
            if (parent) parent.scrollTop = parent.scrollHeight;
        } catch (e) { /* ignore */ }
    }
};
