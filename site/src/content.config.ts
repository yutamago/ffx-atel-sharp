// SPDX-License-Identifier: MIT
import { defineCollection } from 'astro:content';
import { z } from 'astro/zod';
import { docsLoader, i18nLoader } from '@astrojs/starlight/loaders';
import { docsSchema, i18nSchema } from '@astrojs/starlight/schema';

export const collections = {
	docs: defineCollection({
		loader: docsLoader(),
		// sources: the docs/ sections a page is based on (docs/website.md, "The site is written for people").
		schema: docsSchema({ extend: z.object({ sources: z.array(z.string()).optional() }) }),
	}),
	i18n: defineCollection({ loader: i18nLoader(), schema: i18nSchema() }),
};
