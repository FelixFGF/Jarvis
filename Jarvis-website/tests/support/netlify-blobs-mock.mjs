// In-memory test double for the Netlify Blobs methods used by this project.
// It is loaded only by the Node test runner, never bundled into the deployed Functions.
const stores = globalThis.__jarvisBlobTestStores ??= new Map();

export function getStore(options) {
  const name = typeof options === 'string' ? options : options.name;
  if (!stores.has(name)) stores.set(name, new Map());
  const data = stores.get(name);
  return {
    async set(key, value, opts = {}) {
      if (opts.onlyIfNew && data.has(key)) return { modified: false };
      data.set(key, String(value));
      return { modified: true };
    },
    async setJSON(key, value, opts = {}) {
      return this.set(key, JSON.stringify(value), opts);
    },
    async get(key, opts = {}) {
      if (!data.has(key)) return null;
      const value = data.get(key);
      return opts.type === 'json' ? JSON.parse(value) : value;
    },
    async list(opts = {}) {
      const prefix = opts.prefix || '';
      return { blobs: [...data.keys()].filter((key) => key.startsWith(prefix)).map((key) => ({ key, etag: '"mock-etag"' })) };
    },
    async delete(key) { return data.delete(key); }
  };
}
