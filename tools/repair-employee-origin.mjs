// Operator-only repair for a verified legacy account. Dry-run unless --apply is set.
// Uses the operator's Firebase CLI login; never reads or prints passwords/tokens.
import { createRequire } from 'node:module'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import path from 'node:path'

const args = process.argv.slice(2)
const option = (name) => args[args.indexOf(name) + 1]
const required = (name) => {
  if (!args.includes(name) || !option(name) || option(name).startsWith('--')) throw new Error(`Missing ${name}`)
  return option(name)
}
const quote = (value) => `'${String(value).replaceAll("'", "''")}'`
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const apiRoot = path.join(root, 'apps/api')
const require = createRequire(path.join(apiRoot, 'package.json'))
const wrangler = path.join(path.dirname(require.resolve('wrangler/package.json')), 'bin/wrangler.js')

function query(sql) {
  const result = spawnSync(process.execPath, [wrangler, 'd1', 'execute', 'DB', '--remote', '--env', 'production', '--command', sql, '--json'], { cwd: apiRoot, encoding: 'utf8' })
  if (result.status !== 0) throw new Error('D1 command failed; no credential output is printed by this tool')
  return JSON.parse(result.stdout)[0]
}

async function main() {
  const email = required('--email').trim().toLowerCase()
  const uid = required('--uid')
  const organization = required('--organization')
  const project = required('--project')
  const cliLib = required('--firebase-cli-lib')
  const { getGlobalDefaultAccount, getAccessToken } = require(path.join(cliLib, 'auth.js'))
  const account = getGlobalDefaultAccount()
  if (!account) throw new Error('Firebase CLI login required')
  const token = await getAccessToken(account.tokens.refresh_token, ['https://www.googleapis.com/auth/cloud-platform'])
  const response = await fetch(`https://identitytoolkit.googleapis.com/v1/projects/${encodeURIComponent(project)}/accounts:lookup`, {
    method: 'POST', headers: { Authorization: `Bearer ${token.access_token}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: [email] }), signal: AbortSignal.timeout(15000),
  })
  if (!response.ok) throw new Error('Firebase lookup failed')
  const { users } = await response.json()
  const user = users?.length === 1 ? users[0] : null
  if (!user || user.localId !== uid || user.email?.toLowerCase() !== email || user.disabled || user.emailVerified || user.providerUserInfo?.length !== 1 || user.providerUserInfo[0].providerId !== 'password' || typeof user.passwordUpdatedAt !== 'number' || user.passwordUpdatedAt !== Number(user.createdAt)) {
    throw new Error('Firebase account no longer matches the verified initial-issuance state; repair stopped')
  }
  const orgs = query(`SELECT id FROM organizations WHERE id=${quote(organization)} AND setup_completed=1`).results
  if (orgs.length !== 1) throw new Error('Target organization not found')
  const rows = query(`SELECT a.*, s.email FROM auth_accounts a JOIN staff_profiles s ON s.uid=a.uid WHERE a.uid=${quote(uid)} AND lower(s.email)=${quote(email)} AND NOT EXISTS (SELECT 1 FROM organization_memberships m WHERE m.uid=a.uid)`).results
  const current = rows.length === 1 ? rows[0] : null
  if (!current || current.must_change_password !== 1 || current.initial_password_changed_at || current.initial_password_operation || Math.abs(Date.parse(current.initial_password_issued_at) - user.passwordUpdatedAt) > 5000) throw new Error('D1 account no longer matches the verified pending, unassigned state; repair stopped')
  if (current.initial_password_organization_id === organization && current.initial_password_auth_updated_at === user.passwordUpdatedAt) {
    console.log(JSON.stringify({ status: 'already-repaired', uid, organization }))
    return
  }
  if (current.initial_password_organization_id || current.initial_password_auth_updated_at !== null) throw new Error('Existing provenance must not be overwritten')
  if (!args.includes('--apply')) {
    console.log(JSON.stringify({ status: 'dry-run', uid, organization, changes: ['initial_password_organization_id', 'initial_password_auth_updated_at'], passwordChanged: false, membershipRestored: false }))
    return
  }
  const result = query(`UPDATE auth_accounts SET initial_password_organization_id=${quote(organization)}, initial_password_auth_updated_at=${user.passwordUpdatedAt}, updated_at=${quote(new Date().toISOString())}
    WHERE uid=${quote(uid)} AND must_change_password=1 AND initial_password_changed_at IS NULL AND initial_password_operation IS NULL
    AND initial_password_organization_id IS NULL AND initial_password_auth_updated_at IS NULL
    AND updated_at=${quote(current.updated_at)} AND initial_password_issued_at=${quote(current.initial_password_issued_at)}
    AND EXISTS (SELECT 1 FROM staff_profiles WHERE uid=${quote(uid)} AND lower(email)=${quote(email)})
    AND NOT EXISTS (SELECT 1 FROM organization_memberships WHERE uid=${quote(uid)})`)
  if (result.meta.changes !== 1) throw new Error('State changed during repair; no repair applied')
  const verified = query(`SELECT initial_password_organization_id, initial_password_auth_updated_at FROM auth_accounts WHERE uid=${quote(uid)}`).results[0]
  if (verified?.initial_password_organization_id !== organization || verified.initial_password_auth_updated_at !== user.passwordUpdatedAt) throw new Error('Repair requires readback verification')
  console.log(JSON.stringify({ status: 'repaired', uid, organization, passwordChanged: false, membershipRestored: false }))
}

main().catch((error) => {
  // Never serialize upstream error objects (they may contain authorization headers).
  console.error(error instanceof Error ? error.message.split('\n')[0] : 'Repair failed')
  process.exitCode = 1
})
