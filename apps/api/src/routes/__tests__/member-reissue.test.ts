import { env } from 'cloudflare:test'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createDatabase } from '../../db/client'
import { handleMemberRoutes, readdPendingMember } from '../member-routes'
import { completeInitialPasswordChange } from '../../auth/organization'


const uid = 'pending-employee-test'
const org = 'reissue-origin'
const email = 'pending@example.com'
const db = () => createDatabase(env.DB)
const lookup = vi.fn()
const setPassword = vi.fn()
const original = { localId: uid, email, disabled: false, emailVerified: false, passwordUpdatedAt: 1000, providerUserInfo: [{ providerId: 'password' }] }
const readd = () => readdPendingMember(db(), env, org, email, 'Employee', 'new-initial-test-password')

beforeEach(async () => {
  vi.clearAllMocks()
  lookup.mockReset().mockResolvedValue(original)
  setPassword.mockReset().mockImplementation(async () => { lookup.mockResolvedValue({ ...original, passwordUpdatedAt: 2000 }) })
  vi.stubGlobal('fetch', vi.fn(async (url: string, init: RequestInit) => {
    if (url.includes('/accounts:lookup')) return Response.json({ users: [await lookup()] })
    if (url.includes('/accounts:update')) {
      const body = JSON.parse(String(init.body))
      await setPassword(body.localId, body.password)
      return Response.json({ localId: body.localId ?? uid })
    }
    throw new Error('Unexpected external request in test')
  }))
  await env.DB.batch([
    env.DB.prepare('INSERT INTO staff_profiles (uid, display_name, email, role) VALUES (?, ?, ?, ?)').bind(uid, 'Employee', email, 'employee'),
    env.DB.prepare('INSERT INTO auth_accounts (uid, must_change_password, initial_password_organization_id, initial_password_auth_updated_at) VALUES (?, 1, ?, 1000)').bind(uid, org),
    env.DB.prepare("INSERT INTO organization_invites (id, organization_id, email, token_hash, created_by_uid, expires_at) VALUES ('test-invite', ?, ?, 'test-hash', 'admin', '2099-01-01')").bind(org, email),
  ])
})

afterEach(() => vi.unstubAllGlobals())

describe('未利用従業員の再追加', () => {
  it('同じUIDで所属を戻し、招待を無効化してから初期パスワードを返す', async () => {
    const result = await readd()
    expect(result).toMatchObject({ member: { uid, mustChangePassword: true, status: 'active' }, temporaryPassword: 'new-initial-test-password' })
    expect(setPassword).toHaveBeenCalledOnce()
    expect(await env.DB.prepare('SELECT organization_id FROM organization_memberships WHERE uid = ?').bind(uid).first()).toEqual({ organization_id: org })
    expect(await env.DB.prepare('SELECT initial_password_operation, initial_password_auth_updated_at, must_change_password FROM auth_accounts WHERE uid = ?').bind(uid).first()).toEqual({ initial_password_operation: null, initial_password_auth_updated_at: 2000, must_change_password: 1 })
    expect(await env.DB.prepare("SELECT status FROM organization_invites WHERE id = 'test-invite'").first()).toEqual({ status: 'revoked' })
  })

  it.each([
    "initial_password_organization_id = 'another-org'",
    'initial_password_organization_id = NULL',
    'must_change_password = 0',
    "initial_password_changed_at = '2026-01-01'",
    'initial_password_auth_updated_at = NULL',
  ])('再発行権限を証明できない場合は変更しない: %s', async (assignment) => {
    await env.DB.prepare(`UPDATE auth_accounts SET ${assignment} WHERE uid = ?`).bind(uid).run()
    expect(await readd()).toBeNull()
    expect(lookup).not.toHaveBeenCalled()
  })

  it('他組織に所属していたら拒否する', async () => {
    await env.DB.prepare("INSERT INTO organization_memberships (id, organization_id, uid) VALUES ('other-membership', 'another-org', ?)").bind(uid).run()
    await expect(readd()).rejects.toMatchObject({ status: 409 })
    expect(setPassword).not.toHaveBeenCalled()
  })

  it.each([
    { passwordUpdatedAt: 1001 }, { email: 'different@example.com' }, { disabled: true },
    { emailVerified: true }, { providerUserInfo: [{ providerId: 'google.com' }] },
  ])('Firebaseの本人利用・変更があれば拒否する: %j', async (changes) => {
    lookup.mockResolvedValue({ ...original, ...changes })
    await expect(readd()).rejects.toMatchObject({ status: 409 })
    expect(setPassword).not.toHaveBeenCalled()
  })

  it('再発行を二重実行しない', async () => {
    await env.DB.prepare("UPDATE auth_accounts SET initial_password_operation = 'in-progress' WHERE uid = ?").bind(uid).run()
    await expect(readd()).rejects.toMatchObject({ status: 409 })
    expect(setPassword).not.toHaveBeenCalled()
  })

  it('同時の再追加要求でも再発行は一度だけ実行する', async () => {
    const results = await Promise.allSettled([readd(), readd()])
    expect(results.filter((result) => result.status === 'fulfilled')).toHaveLength(1)
    expect(results.filter((result) => result.status === 'rejected')).toHaveLength(1)
    expect(setPassword).toHaveBeenCalledOnce()
    expect(await env.DB.prepare('SELECT count(*) AS count FROM organization_memberships WHERE uid = ?').bind(uid).first()).toEqual({ count: 1 })
  })

  it('Firebaseの結果が不明な場合は所属と成功レスポンスを作らない', async () => {
    setPassword.mockRejectedValue(new Error('network'))
    await expect(readd()).rejects.toThrow()
    expect(await env.DB.prepare('SELECT uid FROM organization_memberships WHERE uid = ?').bind(uid).first()).toBeNull()
    const row = await env.DB.prepare('SELECT initial_password_operation FROM auth_accounts WHERE uid = ?').bind(uid).first<{ initial_password_operation: string }>()
    expect(row?.initial_password_operation).toBeTruthy()
    await expect(readd()).rejects.toMatchObject({ status: 409 })
    expect(setPassword).toHaveBeenCalledOnce()
  })

  it('Firebase成功後のD1失敗でも所属・招待の部分更新を残さない', async () => {
    await env.DB.prepare("CREATE TRIGGER fail_reissue BEFORE UPDATE ON staff_profiles WHEN NEW.uid = 'pending-employee-test' BEGIN SELECT RAISE(ABORT, 'test failure'); END").run()
    await expect(readd()).rejects.toThrow()
    expect(await env.DB.prepare('SELECT uid FROM organization_memberships WHERE uid = ?').bind(uid).first()).toBeNull()
    expect(await env.DB.prepare("SELECT status FROM organization_invites WHERE id = 'test-invite'").first()).toEqual({ status: 'pending' })
    await env.DB.prepare('DROP TRIGGER fail_reissue').run()
  })

  it('所属のないアカウントは再発行と並行して初回変更できない', async () => {
    await expect(completeInitialPasswordChange(db(), env, uid, 'unused-token', 'unused-password')).rejects.toMatchObject({ status: 409 })
  })

  it('本人が初回変更を完了したらラベルを消し、再発行を拒否する', async () => {
    await readd()
    await completeInitialPasswordChange(db(), env, uid, 'test-id-token', 'personal-password')
    expect(await env.DB.prepare('SELECT must_change_password, initial_password_operation FROM auth_accounts WHERE uid = ?').bind(uid).first()).toEqual({ must_change_password: 0, initial_password_operation: null })
    await env.DB.prepare('DELETE FROM organization_memberships WHERE uid = ?').bind(uid).run()
    setPassword.mockClear()
    expect(await readd()).toBeNull()
    expect(setPassword).not.toHaveBeenCalled()
  })

  it('管理者設定がなければロックやパスワード変更を行わない', async () => {
    await expect(readdPendingMember(db(), { ...env, APP_ENV: 'production', FIREBASE_ADMIN_SERVICE_ACCOUNT_JSON: undefined }, org, email, 'Employee', 'new-password')).rejects.toMatchObject({ status: 503 })
    expect(setPassword).not.toHaveBeenCalled()
    expect(await env.DB.prepare('SELECT initial_password_operation FROM auth_accounts WHERE uid = ?').bind(uid).first()).toEqual({ initial_password_operation: null })
  })

  it('新規登録も所属と初期パスワードの状態が揃ってから成功を返す', async () => {
    const actor = 'reissue-admin'
    const newUid = 'brand-new-employee'
    await env.DB.batch([
      env.DB.prepare('INSERT INTO organizations (id, name) VALUES (?, ?)').bind(org, 'Test organization'),
      env.DB.prepare("INSERT INTO organization_memberships (id, organization_id, uid, role) VALUES ('admin-member', ?, ?, 'owner')").bind(org, actor),
    ])
    const project = env.FIREBASE_PROJECT_ID
    const token = [btoa(JSON.stringify({ alg: 'none' })), btoa(JSON.stringify({ sub: actor, aud: project, iss: `https://securetoken.google.com/${project}`, exp: Math.floor(Date.now() / 1000) + 3600, email: 'admin@example.com', email_verified: true })), 'test'].join('.')
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url.includes('accounts:signUp')) return Response.json({ localId: newUid, idToken: 'signup-token' })
      if (url.includes('accounts:lookup')) return Response.json({ users: [{ localId: newUid, passwordUpdatedAt: 3000 }] })
      throw new Error('Unexpected request')
    }))
    const response = await handleMemberRoutes(new Request('https://test/api/organization/members', {
      method: 'POST', headers: { Authorization: `Bearer ${token}`, 'X-Organization-Id': org, 'Content-Type': 'application/json' },
      body: JSON.stringify({ displayName: 'New Employee', email: 'new@example.com' }),
    }), env)
    expect(response?.status).toBe(201)
    expect(await response?.json()).toMatchObject({ member: { uid: newUid, email: 'new@example.com', mustChangePassword: true }, temporaryPassword: expect.any(String) })
    expect(await env.DB.prepare('SELECT initial_password_organization_id, initial_password_auth_updated_at FROM auth_accounts WHERE uid = ?').bind(newUid).first()).toEqual({ initial_password_organization_id: org, initial_password_auth_updated_at: 3000 })
    expect(await env.DB.prepare('SELECT organization_id FROM organization_memberships WHERE uid = ?').bind(newUid).first()).toEqual({ organization_id: org })
  })
})
