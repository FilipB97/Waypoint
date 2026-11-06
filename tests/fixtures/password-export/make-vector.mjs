// Niezależna implementacja formatu „waypoint-passwords” v1 (docs/specs/password-export.md) w Node `crypto`.
// Generuje vector-v1.json, który muszą odszyfrować obie wersje (C# i Swift). Stała sól i nonce,
// więc wynik jest powtarzalny: `node make-vector.mjs` nie powinien zmieniać pliku.
import { pbkdf2Sync, createCipheriv } from "node:crypto";
import { writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const filePassword = "zażółć gęślą jaźń";
const iterations = 1000;
const salt = Buffer.from("000102030405060708090a0b0c0d0e0f", "hex");
const nonce = Buffer.from("a0a1a2a3a4a5a6a7a8a9aaab", "hex");
const passwords = {
  "srv-0001": "Hasło-Żółw#1",
  "profile:prof-0001": "zażółć",
};

const key = pbkdf2Sync(Buffer.from(filePassword, "utf8"), salt, iterations, 32, "sha256");
const cipher = createCipheriv("aes-256-gcm", key, nonce);
const plain = Buffer.from(JSON.stringify({ Passwords: passwords }), "utf8");
const data = Buffer.concat([cipher.update(plain), cipher.final(), cipher.getAuthTag()]);

const file = {
  Format: "waypoint-passwords",
  Version: 1,
  Kdf: "PBKDF2-SHA256",
  Iterations: iterations,
  Salt: salt.toString("base64"),
  Nonce: nonce.toString("base64"),
  Data: data.toString("base64"),
};
writeFileSync(fileURLToPath(new URL("vector-v1.json", import.meta.url)), JSON.stringify(file, null, 2) + "\n", "utf8");
