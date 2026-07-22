// Generates the extension's toolbar icons: a dark rounded square with a green ring, so the
// button is findable among a row of extensions. Run: node tools/make-icons.mjs
//
// Hand-rolled PNG encoder to keep the repo dependency-free — it only needs one image shape.

import { deflateSync } from "node:zlib";
import { writeFileSync, mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const OUT = join(dirname(fileURLToPath(import.meta.url)), "..", "extension", "icons");

const BG = [32, 34, 38, 255];      // near-black chip
const RING = [46, 160, 67, 255];   // same green as the "armed" badge

function crc32(buf) {
  let table = crc32.table;
  if (!table) {
    table = crc32.table = new Int32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      table[n] = c;
    }
  }
  let crc = -1;
  for (const byte of buf) crc = (crc >>> 8) ^ table[(crc ^ byte) & 0xff];
  return (crc ^ -1) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([len, body, crc]);
}

function encodePng(size, pixel) {
  // Raw scanlines, each prefixed with filter type 0.
  const raw = Buffer.alloc(size * (size * 4 + 1));
  let o = 0;
  for (let y = 0; y < size; y++) {
    raw[o++] = 0;
    for (let x = 0; x < size; x++) {
      const [r, g, b, a] = pixel(x, y, size);
      raw[o++] = r; raw[o++] = g; raw[o++] = b; raw[o++] = a;
    }
  }

  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8;   // bit depth
  ihdr[9] = 6;   // colour type: RGBA
  ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr),
    chunk("IDAT", deflateSync(raw, { level: 9 })),
    chunk("IEND", Buffer.alloc(0)),
  ]);
}

// Coverage-based antialiasing: sample a 3x3 grid per pixel and average.
function coverage(x, y, test) {
  let hits = 0;
  for (let sy = 0; sy < 3; sy++)
    for (let sx = 0; sx < 3; sx++)
      if (test(x + (sx + 0.5) / 3, y + (sy + 0.5) / 3)) hits++;
  return hits / 9;
}

function blend(under, over, alpha) {
  return under.map((c, i) => Math.round(c * (1 - alpha) + over[i] * alpha));
}

function pixel(x, y, size) {
  const s = size / 128;
  const cx = size / 2, cy = size / 2;

  // Rounded-square chip.
  const pad = 6 * s, r = 26 * s;
  const inSquare = (px, py) => {
    const dx = Math.max(pad + r - px, 0, px - (size - pad - r));
    const dy = Math.max(pad + r - py, 0, py - (size - pad - r));
    return px >= pad && px <= size - pad && py >= pad && py <= size - pad &&
           dx * dx + dy * dy <= r * r;
  };

  // Ring, with a gap on the right so it reads as a target rather than a plain circle.
  const outer = 38 * s, inner = 26 * s;
  const inRing = (px, py) => {
    const dx = px - cx, dy = py - cy;
    const d = Math.hypot(dx, dy);
    if (d > outer || d < inner) return false;
    const angle = Math.atan2(dy, dx);
    return Math.abs(angle) > 0.42;
  };

  const dot = 11 * s;
  const inDot = (px, py) => Math.hypot(px - cx, py - cy) <= dot;

  const squareA = coverage(x, y, inSquare);
  if (squareA === 0) return [0, 0, 0, 0];

  let colour = BG.slice(0, 3);
  colour = blend(colour, RING.slice(0, 3), coverage(x, y, inRing));
  colour = blend(colour, RING.slice(0, 3), coverage(x, y, inDot));

  return [...colour, Math.round(255 * squareA)];
}

mkdirSync(OUT, { recursive: true });
for (const size of [16, 32, 48, 128]) {
  const file = join(OUT, `icon${size}.png`);
  writeFileSync(file, encodePng(size, pixel));
  console.log(`wrote ${file}`);
}
