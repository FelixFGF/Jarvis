import { register } from 'node:module';
register(new URL('./netlify-blobs-loader.mjs', import.meta.url), import.meta.url);
