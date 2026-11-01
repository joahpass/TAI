// Invoke exactly the verification shipped inside the installed package.
import path from 'node:path';
import { pathToFileURL } from 'node:url';
if(!process.argv[2])throw new Error('Usage: node smoke-installed.mjs INSTALL_ROOT');
const root=path.resolve(process.argv[2]);
process.argv.splice(2);
await import(pathToFileURL(path.join(root,'installer/verify-install.mjs')).href);
