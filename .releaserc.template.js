// Shared Semantic-Release Configuration for All Templates
// Uses environment variables to customize for each specific template

// Read environment variables provided by GitHub Actions workflow
const templateName = process.env.TEMPLATE_NAME;
const templatePath = process.env.TEMPLATE_PATH;

// Validate required environment variables
if (!templateName || !templatePath) {
  console.error('ERROR: Missing required environment variables for template release:');
  console.error('  TEMPLATE_NAME:', templateName || '(not set)');
  console.error('  TEMPLATE_PATH:', templatePath || '(not set)');
  console.error('\nThese variables must be set by the calling workflow.');
  process.exit(1);
}

// Log configuration for debugging
console.log('Template Release Configuration:');
console.log('  Template Name:', templateName);
console.log('  Template Path:', templatePath);

// Construct template-specific values
const tagFormat = `${templateName}-v\${version}`;
const changelogFile = `${templatePath}/CHANGELOG.md`;
const changelogBackup = `${changelogFile}.backup`;

console.log('  Tag Format:', tagFormat);
console.log('  Changelog:', changelogFile);
console.log('');

// Export semantic-release configuration
module.exports = {
  tagFormat,
  branches: [
    'main',
    {
      name: 'vnext',
      prerelease: 'rc'
    },
    {
      name: 'develop',
      prerelease: 'dev'
    }
  ],
  plugins: [
    // Analyze commits using conventional commits
    [
      '@semantic-release/commit-analyzer',
      {
        preset: 'conventionalcommits',
        releaseRules: [
          { type: 'feat', release: 'minor' },
          { type: 'fix', release: 'patch' },
          { type: 'perf', release: 'patch' },
          { type: 'revert', release: 'patch' },
          { type: 'docs', scope: 'README', release: 'patch' },
          { type: 'style', release: false },
          { type: 'chore', release: false },
          { type: 'refactor', release: false },
          { type: 'test', release: false },
          { type: 'build', release: false },
          { type: 'ci', release: false },
          { breaking: true, release: 'major' },
          { revert: true, release: 'patch' }
        ],
        parserOpts: {
          noteKeywords: ['BREAKING CHANGE', 'BREAKING CHANGES', 'BREAKING']
        }
      }
    ],

    // Generate release notes
    [
      '@semantic-release/release-notes-generator',
      {
        preset: 'conventionalcommits',
        presetConfig: {
          types: [
            { type: 'feat', section: '🚀 Features', hidden: false },
            { type: 'fix', section: '🐛 Bug Fixes', hidden: false },
            { type: 'perf', section: '⚡ Performance Improvements', hidden: false },
            { type: 'revert', section: '⏪ Reverts', hidden: false },
            { type: 'docs', section: '📚 Documentation', hidden: false },
            { type: 'style', section: '💄 Styles', hidden: false },
            { type: 'chore', section: '🔧 Chores', hidden: false },
            { type: 'refactor', section: '♻️ Code Refactoring', hidden: false },
            { type: 'test', section: '✅ Tests', hidden: false },
            { type: 'build', section: '📦 Build System', hidden: false },
            { type: 'ci', section: '👷 CI/CD', hidden: false }
          ]
        }
      }
    ],

    // Backup changelog for pre-releases
    [
      '@semantic-release/exec',
      {
        verifyConditionsCmd: `if [ '\${branch.prerelease}' != 'false' ] && [ '\${branch.prerelease}' != '' ]; then cp ${changelogFile} ${changelogBackup} 2>/dev/null || true; fi`,
        prepareCmd: `echo '\${nextRelease.version}' > .version && if [ '\${nextRelease.channel}' = '' ]; then ./scripts/update-version.sh \${nextRelease.version} ${templateName}; else echo 'Skipping version update for pre-release \${nextRelease.version}'; fi`
      }
    ],

    // Update changelog
    [
      '@semantic-release/changelog',
      {
        changelogFile
      }
    ],

    // Restore changelog for pre-releases
    [
      '@semantic-release/exec',
      {
        prepareCmd: `if [ '\${nextRelease.channel}' != '' ] && [ -f ${changelogBackup} ]; then mv ${changelogBackup} ${changelogFile}; echo 'Restored CHANGELOG.md for pre-release'; fi`
      }
    ],

    // Commit changes
    [
      '@semantic-release/git',
      {
        assets: [
          changelogFile,
          `${templatePath}/*.csproj`
        ],
        message: `chore(release:${templateName}): \${nextRelease.version} [skip ci]\n\n\${nextRelease.notes}`
      }
    ],

    // Create GitHub release
    [
      '@semantic-release/github',
      {
        successComment: false,
        failComment: false
      }
    ]
  ]
};
