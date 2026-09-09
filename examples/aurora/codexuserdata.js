// CodexUserData shape API v1. Available only inside the application's HTML host.
(() => {
  const listeners = new Set();
  let latest = null;
  const send = message => window.chrome?.webview?.postMessage(message);
  window.CodexUserData = Object.freeze({
    subscribe(listener) { listeners.add(listener); if (latest) listener(latest); return () => listeners.delete(listener); },
    drag: () => send({type: 'drag'}),
    resize: (width, height) => send({type: 'resize', width, height}),
    restoreMain: () => send({type: 'restoreMain'})
  });
  window.chrome?.webview?.addEventListener('message', event => {
    const message = event.data;
    if (message?.type !== 'snapshot' || message.apiVersion !== 1) return;
    latest = message.data;
    for (const listener of listeners) { try { listener(latest); } catch (error) { console.error(error); } }
  });
  send({type: 'ready'});
})();
