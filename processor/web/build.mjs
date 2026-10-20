import { build, formatMessages } from 'esbuild';
import { mkdir, copyFile, readFile, writeFile } from 'node:fs/promises';

await mkdir('dist', { recursive: true });
const result = await build({ entryPoints: ['src/app.tsx'], bundle: true, minify: true, sourcemap: false,
  outfile: 'dist/app.js', target: ['es2022'], platform: 'browser',
  define: { 'process.env.NODE_ENV': '"production"' }, logLevel: 'silent' });
if (result.warnings.length) throw new Error((await formatMessages(result.warnings, { kind: 'warning', color: false })).join('\n'));
await copyFile('index.html', 'dist/index.html');
// Convert the existing, repository-owned Android vector. Do not substitute new branding.
const vector = await readFile('../../app/src/main/res/drawable/ic_launcher_foreground.xml', 'utf8');
const paths = [...vector.matchAll(/<path\b([^>]*?)\/>/gs)];
if (paths.length !== 5) throw new Error('Original MPA vector structure changed; review its conversion.');
const attribute = (source, name, fallback) => {
  const value = source.match(new RegExp('android:' + name + '="([^"]*)"'))?.[1] ?? fallback;
  if (value === undefined || /[<>&"]/.test(value)) throw new Error('Unsupported branding attribute.');
  return value;
};
const converted = paths.map(([, source]) => '<path d="' + attribute(source, 'pathData') + '" fill="' + attribute(source, 'fillColor', 'none').replace('#00000000', 'none') +
  '" stroke="' + attribute(source, 'strokeColor', 'none') + '" stroke-width="' + attribute(source, 'strokeWidth', '0') + '" stroke-linecap="round" stroke-linejoin="round"/>').join('');
await writeFile('dist/mpa.svg', '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 108 108"><rect width="108" height="108" rx="24" fill="#125E53"/>' + converted + '</svg>');
