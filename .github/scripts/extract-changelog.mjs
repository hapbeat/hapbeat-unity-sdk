#!/usr/bin/env node
// Extract the section for a given version tag from CHANGELOG.md and write it
// to release-notes.md (consumed by the release workflow).
import { readFileSync, writeFileSync } from 'fs';

const tag = process.argv[2]; // e.g. "v0.1.0"
if (!tag) { console.error('Usage: extract-changelog.mjs <tag>'); process.exit(1); }

const version = tag.replace(/^v/, '');
const changelog = readFileSync('CHANGELOG.md', 'utf8');
const lines = changelog.split('\n');

let inside = false;
const notes = [];

for (const line of lines) {
  if (!inside && line.startsWith(`## [${version}]`)) {
    inside = true;
    continue; // skip the heading itself
  }
  if (inside && /^## \[/.test(line)) break; // next version section
  if (inside) notes.push(line);
}

if (notes.length === 0) {
  console.error(`No section found for version ${version} in CHANGELOG.md`);
  process.exit(1);
}

const body = notes.join('\n').trim();
writeFileSync('release-notes.md', body);
console.log(`Extracted ${notes.length} lines for ${version}`);
