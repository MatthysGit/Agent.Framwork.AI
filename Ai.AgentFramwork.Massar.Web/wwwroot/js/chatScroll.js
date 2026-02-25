window.chatScroll = {

    smoothToBottom: function (el) {
        if (!el) return;
        el.scrollTo({
            top: el.scrollHeight,
            behavior: 'smooth'
        });
    },

    instantToBottom: function (el) {
        if (!el) return;
        el.scrollTop = el.scrollHeight;
    },

    isNearBottom: function (el, threshold) {
        if (!el) return true;
        threshold = threshold || 120;
        return (el.scrollHeight - el.scrollTop - el.clientHeight) < threshold;
    }
};