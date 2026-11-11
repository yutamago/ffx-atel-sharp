// SPDX-License-Identifier: MIT
// Writes public/og.png, the social preview image (1200x630) the site's pages name in their meta tags.
// Run it again after changing the text: `npm run og`. Uses the sharp package the site already depends on.
import sharp from 'sharp';
import { fileURLToPath } from 'node:url';

const out = fileURLToPath(new URL('../public/og.png', import.meta.url));

const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="630" viewBox="0 0 1200 630">
	<rect width="1200" height="630" fill="#17181c"/>
	<rect x="0" y="0" width="24" height="630" fill="#8b7bf7"/>
	<text x="110" y="300" font-family="DejaVu Sans, Arial, sans-serif" font-size="150" font-weight="700" fill="#ffffff">Atel#</text>
	<text x="114" y="392" font-family="DejaVu Sans, Arial, sans-serif" font-size="42" fill="#d4d4dc">Final Fantasy X's scripts as C#.</text>
	<text x="114" y="452" font-family="DejaVu Sans, Arial, sans-serif" font-size="42" fill="#d4d4dc">Write mods, compile them back into the game.</text>
	<text x="114" y="560" font-family="DejaVu Sans Mono, monospace" font-size="28" fill="#9a9aa8">yutamago.github.io/ffx-atel-sharp</text>
</svg>`;

await sharp(Buffer.from(svg)).png({ compressionLevel: 9 }).toFile(out);
console.log(`wrote ${out}`);
