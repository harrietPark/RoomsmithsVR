// Keep one collection backend and device identity on both public addresses.
export default {
  fetch(request, env) {
    if (new URL(request.url).pathname.startsWith('/api/collect/')) return env.COLLECTION.fetch(request);
    return env.ASSETS.fetch(request);
  }
};
