// Redirect the Netlify SDK import to an in-memory API test double during `npm test` only.
export async function resolve(specifier, context, nextResolve) {
  if (specifier === '@netlify/blobs') {
    return { url: new URL('./support/netlify-blobs-mock.mjs', import.meta.url).href, shortCircuit: true };
  }
  return nextResolve(specifier, context);
}
