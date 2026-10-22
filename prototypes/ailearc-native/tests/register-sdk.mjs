import { registerHooks } from 'node:module';
const sdk = new URL('../../../.tools/native-sdk-source/packages/core/sdk/', import.meta.url);
registerHooks({
  resolve(specifier, context, next) {
    if (specifier === '@native-sdk/core') return { url: new URL('core.ts', sdk).href, shortCircuit: true };
    if (specifier === '@native-sdk/core/text') return { url: new URL('text.ts', sdk).href, shortCircuit: true };
    return next(specifier, context);
  },
});
