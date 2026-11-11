// SPDX-License-Identifier: MIT
// The site at https://yutamago.github.io/ffx-atel-sharp/ (docs/website.md).
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';

export default defineConfig({
	site: 'https://yutamago.github.io',
	base: '/ffx-atel-sharp',
	integrations: [
		starlight({
			title: 'Atel#',
			description:
				"Atel# decompiles Final Fantasy X's event and battle scripts to C#, and compiles your C# mods back into the game through Fahrenheit.",
			customCss: ['./src/styles/custom.css'],
			// The social preview image (public/og.png, written by scripts/og.mjs); Starlight sets the other og: tags.
			head: [
				{ tag: 'meta', attrs: { property: 'og:image', content: 'https://yutamago.github.io/ffx-atel-sharp/og.png' } },
				{ tag: 'meta', attrs: { property: 'og:image:width', content: '1200' } },
				{ tag: 'meta', attrs: { property: 'og:image:height', content: '630' } },
				{ tag: 'meta', attrs: { property: 'og:image:alt', content: "Atel#: Final Fantasy X's scripts as C#." } },
				{ tag: 'meta', attrs: { name: 'twitter:image', content: 'https://yutamago.github.io/ffx-atel-sharp/og.png' } },
			],
			// The 404 page is src/content/docs/404.md.
			disable404Route: true,
			plugins: [starlightLinksValidator()],
			social: [
				{ icon: 'github', label: 'GitHub', href: 'https://github.com/yutamago/ffx-atel-sharp' },
			],
			sidebar: [
				{ label: 'Get started', items: [{ autogenerate: { directory: 'get-started' } }] },
				{ label: 'Guides', items: [{ autogenerate: { directory: 'guides' } }] },
				{ label: 'Concepts', items: [{ autogenerate: { directory: 'concepts' } }] },
				{ label: 'Reference', items: [{ autogenerate: { directory: 'reference' } }] },
				{ label: 'How it works', items: [{ autogenerate: { directory: 'how-it-works' } }] },
				{ label: 'Project', items: [{ autogenerate: { directory: 'project' } }] },
			],
		}),
	],
});
