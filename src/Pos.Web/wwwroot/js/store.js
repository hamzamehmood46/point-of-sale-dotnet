// Tiny wrapper around localStorage. Storage can be blocked (private windows, strict settings), so every call is guarded.
window.tillStore = {
  get: function (key) { try { return window.localStorage.getItem(key); } catch (e) { return null; } },
  tzOffsetMinutes: function () { return -new Date().getTimezoneOffset(); },
  set: function (key, value) { try { window.localStorage.setItem(key, value); } catch (e) { /* storage unavailable: carry on */ } }
};
