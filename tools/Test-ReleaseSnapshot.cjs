const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const root = path.resolve(__dirname, '..');
const forbiddenDirs = new Set(['.git', '.vs', '.wix', '.harness', 'bin', 'obj', 'payload', 'output', 'verification', 'node_modules']);
const textExt = new Set(['.cs', '.csproj', '.props', '.targets', '.ps1', '.cjs', '.js', '.json', '.xml', '.xaml', '.wxs', '.iss', '.addin', '.config', '.md', '.txt', '.cmd', '.sln']);
const secrets = [
  /sk-or-v1-[A-Za-z0-9]{20,}|sk-[A-Za-z0-9]{32,}/,
  /Authorization\s*[:=]\s*["']?Bearer\s+[A-Za-z0-9._-]{24,}/i,
  /(password|secret|api[_-]?key)\s*[:=]\s*["'][^"'\s]{16,}["']/i,
  /-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----/
];
let count = 0;
function walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (forbiddenDirs.has(entry.name)) throw new Error(`Forbidden generated directory: ${path.relative(root, full)}`);
      if (entry.name === path.basename(root)) throw new Error('Nested release-source folder');
      walk(full);
    } else if (entry.isFile()) {
      if (textExt.has(path.extname(entry.name).toLowerCase())) {
        const body = fs.readFileSync(full, 'utf8');
        if (secrets.some(pattern => pattern.test(body))) throw new Error(`Potential secret in ${path.relative(root, full)} (value suppressed)`);
        if (/\.(csproj|props|wxs|iss|ps1)$/.test(entry.name) && /C:\\Users\\|E:\\BuildAI\\/i.test(body)) {
          throw new Error(`Developer-specific absolute source path in ${path.relative(root, full)}`);
        }
      }
      count++;
    }
  }
}
walk(root);
const sums = fs.readFileSync(path.join(root, 'SHA256SUMS.txt'), 'utf8').trim().split(/\r?\n/);
for (const line of sums) {
  const match = /^([a-f0-9]{64})  (.+)$/.exec(line);
  if (!match) throw new Error('Malformed SHA256SUMS entry');
  const file = path.resolve(root, match[2]);
  if (!file.startsWith(root + path.sep)) throw new Error('Checksum path escapes release root');
  const actual = crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  if (actual !== match[1]) throw new Error(`Checksum mismatch: ${match[2]}`);
}
for (const name of [ 'BuildAI_RevitPlugins_7.4_Setup.exe', 'BuildAI_AccIssueReturn_1.1_Setup.exe']) {
  if (!fs.existsSync(path.join(root, 'Artifacts', name))) throw new Error(`Installer missing: ${name}`);
}
console.log(`Release snapshot verified: ${count} files, ${sums.length} SHA-256 entries, no generated directories or detected secrets.`);

const allowed=new Set(['BuildAI_RevitPlugins_7.4_Setup.exe','BuildAI_AccIssueReturn_1.1_Setup.exe','SHA256SUMS.txt','README_INSTALL.txt']);
for(const entry of fs.readdirSync(path.join(root,'Artifacts'),{withFileTypes:true})){if(!entry.isFile() || !allowed.has(entry.name))throw new Error('Unexpected user artifact: '+entry.name);}
