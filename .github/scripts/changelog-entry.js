'use strict';

// The text of one version's section in CHANGELOG.md: everything under its `## <version>` heading,
// up to the next `## ` heading.
function entryFor(changelog, version) {
    const lines = changelog.split('\n');
    // Keep a Changelog writes the version bracketed (`## [0.2.0] — date`); both forms are accepted.
    const headings = ['## ' + version, '## [' + version + ']'];
    const start = lines.findIndex(line => headings.some(h => line === h || line.startsWith(h + ' ')));
    if (start < 0) throw new Error(`CHANGELOG.md has no section for ${version}`);
    const rest = lines.slice(start + 1);
    const end = rest.findIndex(line => line.startsWith('## '));
    const entry = rest.slice(0, end < 0 ? rest.length : end).join('\n').trim();
    if (!entry) throw new Error(`CHANGELOG.md has an empty section for ${version}`);
    return entry;
}

// build.yaml with its `changelog` line replaced. JSON.stringify yields a valid YAML double-quoted
// scalar, so the entry's line breaks and quotes survive JPRM's YAML parser unchanged.
function withChangelog(buildYaml, entry) {
    if (!/^changelog:/m.test(buildYaml)) throw new Error('build.yaml has no changelog line');
    return buildYaml.replace(/^changelog:.*$/m, () => 'changelog: ' + JSON.stringify(entry));
}

module.exports = { entryFor, withChangelog };

// Release workflow entry point, run from the repository root:
//   node .github/scripts/changelog-entry.js <version>                  writes build.yaml's changelog
//   node .github/scripts/changelog-entry.js <version> --notes <file>   writes the GitHub Release text
// Any error is uncaught on purpose, so the step, and the release, fails.
if (require.main === module) {
    const fs = require('node:fs');
    const [version, flag, notes] = process.argv.slice(2);
    const entry = entryFor(fs.readFileSync('CHANGELOG.md', 'utf8'), version);
    if (flag === '--notes') {
        fs.writeFileSync(notes, entry);
        console.log(`${notes} set from the CHANGELOG.md section for ${version}`);
    } else {
        fs.writeFileSync('build.yaml', withChangelog(fs.readFileSync('build.yaml', 'utf8'), entry));
        console.log(`build.yaml changelog set from the CHANGELOG.md section for ${version}`);
    }
}
