import { spawnSync } from 'node:child_process'
import { existsSync, readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const apiDirectory = path.join(repositoryRoot, 'apps', 'api')
const varsPath = path.join(apiDirectory, '.dev.vars')

function fail(message) {
  console.error(`[development-seed] ${message}`)
  process.exit(1)
}

if (process.argv.length > 2) {
  fail('引数は受け付けません。開発用ローカルD1だけへシードを適用します。')
}

if (!existsSync(varsPath)) {
  fail('apps/api/.dev.vars がありません。.dev.vars.example をコピーして設定してください。')
}

const variables = parseVars(readFileSync(varsPath, 'utf8'))
if (variables.APP_ENV !== 'development') {
  fail('APP_ENV=development のときだけ実行できます。')
}
if (variables.FIREBASE_AUTH_EMULATOR !== 'true') {
  fail('FIREBASE_AUTH_EMULATOR=true のときだけ実行できます。')
}
if (!variables.FIREBASE_PROJECT_ID) {
  fail('FIREBASE_PROJECT_ID が未設定です。')
}
if (variables.DATA_ENV === 'production') {
  fail('DATA_ENV=production の環境では開発用シードを実行できません。')
}

const pnpmCommand = process.platform === 'win32' ? 'pnpm.cmd' : 'pnpm'
const result = spawnSync(pnpmCommand, ['exec', 'wrangler', 'd1', 'execute', 'DB', '--local', '--file', 'seed/dev.sql'], {
  cwd: apiDirectory,
  stdio: 'inherit',
  shell: process.platform === 'win32',
})

if (result.error) fail(`Wranglerの実行に失敗しました: ${result.error.message}`)
process.exit(result.status ?? 1)

function parseVars(contents) {
  const variables = {}
  for (const line of contents.split(/\r?\n/)) {
    const normalized = line.trim().replace(/^export\s+/, '')
    if (!normalized || normalized.startsWith('#')) continue
    const separator = normalized.indexOf('=')
    if (separator < 1) continue
    const key = normalized.slice(0, separator).trim()
    let value = normalized.slice(separator + 1).trim()
    if ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'"))) {
      value = value.slice(1, -1)
    }
    variables[key] = value
  }
  return variables
}
