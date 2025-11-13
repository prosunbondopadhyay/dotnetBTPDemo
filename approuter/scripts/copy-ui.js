const fs = require('fs');
const path = require('path');

function copyDir(src, dst) {
  if (!fs.existsSync(src)) return;
  fs.mkdirSync(dst, { recursive: true });
  for (const entry of fs.readdirSync(src, { withFileTypes: true })) {
    const s = path.join(src, entry.name);
    const d = path.join(dst, entry.name);
    if (entry.isDirectory()) copyDir(s, d);
    else fs.copyFileSync(s, d);
  }
}

const rootUi = path.resolve(__dirname, '../../app/dist');
const target = path.resolve(__dirname, '../resources/app');
copyDir(rootUi, target);
console.log(`Copied UI from ${rootUi} to ${target}`);
