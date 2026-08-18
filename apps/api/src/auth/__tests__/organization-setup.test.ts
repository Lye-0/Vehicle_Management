import { env } from 'cloudflare:test'
import { afterAll, describe, expect, it } from 'vitest'
import { createDatabase } from '../../db/client'
import { completeInitialOrganizationSetup, loadAuthSession } from '../organization'

const organizationId = 'org-initial-setup-batch-test'
const uid = 'uid-initial-setup-batch-test'

describe('initial organization setup', () => {
  afterAll(async () => {
    await env.DB.prepare('DELETE FROM organization_memberships WHERE organization_id = ?').bind(organizationId).run()
    await env.DB.prepare('DELETE FROM organizations WHERE id = ?').bind(organizationId).run()
    await env.DB.prepare('DELETE FROM staff_profiles WHERE uid = ?').bind(uid).run()
    await env.DB.prepare('DELETE FROM auth_accounts WHERE uid = ?').bind(uid).run()
  })

  it('writes the organization, membership, profile, and auth account as one batch', async () => {
    await env.DB.prepare('INSERT INTO organizations (id, name, setup_completed, created_at, updated_at) VALUES (?, ?, 0, ?, ?)').bind(organizationId, 'セットアップ前', '0000-01-01T00:00:00.000Z', '0000-01-01T00:00:00.000Z').run()
    const user = { uid, email: 'setup@example.invalid', displayName: 'セットアップユーザー', emailVerified: true, isAnonymous: false }
    const database = createDatabase(env.DB)
    const setupEnv = { ...env, APP_ENV: 'test', INITIAL_SETUP_KEY: 'setup-test-key' } as Env

    await completeInitialOrganizationSetup(database, setupEnv, user, 'セットアップ後', 'setup-test-key')

    const organization = await env.DB.prepare('SELECT name, owner_uid, setup_completed FROM organizations WHERE id = ?').bind(organizationId).first<{ name: string; owner_uid: string; setup_completed: number }>()
    const membership = await env.DB.prepare('SELECT role, status FROM organization_memberships WHERE organization_id = ? AND uid = ?').bind(organizationId, uid).first<{ role: string; status: string }>()
    const profile = await env.DB.prepare('SELECT display_name, email, role FROM staff_profiles WHERE uid = ?').bind(uid).first<{ display_name: string; email: string; role: string }>()
    const account = await env.DB.prepare('SELECT uid FROM auth_accounts WHERE uid = ?').bind(uid).first<{ uid: string }>()

    expect(organization).toEqual({ name: 'セットアップ後', owner_uid: uid, setup_completed: 1 })
    expect(membership).toEqual({ role: 'owner', status: 'active' })
    expect(profile).toEqual({ display_name: 'セットアップユーザー', email: 'setup@example.invalid', role: 'owner' })
    expect(account).toEqual({ uid })
  })

  it('recreates the development organization when it is missing from an existing local database', async () => {
    const defaultOrganization = await env.DB.prepare('SELECT id, name, owner_uid, setup_completed FROM organizations WHERE id = ?').bind('org-default').first<{ id: string; name: string; owner_uid: string | null; setup_completed: number }>()
    await env.DB.prepare('DELETE FROM organizations WHERE id = ?').bind('org-default').run()

    try {
      const user = { uid: 'uid-development-anonymous-test', email: null, displayName: null, emailVerified: false, isAnonymous: true }
      const database = createDatabase(env.DB)
      const developmentEnv = { ...env, APP_ENV: 'development', FIREBASE_AUTH_EMULATOR: 'true' } as Env

      const session = await loadAuthSession(database, developmentEnv, user)

      expect(session.organizations).toEqual([{ id: 'development-anonymous-org-default', organizationId: 'org-default', name: '東京都心支店', role: 'owner', status: 'active' }])
      await expect(env.DB.prepare('SELECT id, name, owner_uid, setup_completed FROM organizations WHERE id = ?').bind('org-default').first()).resolves.toEqual({ id: 'org-default', name: '東京都心支店', owner_uid: null, setup_completed: 0 })
    } finally {
      await env.DB.prepare('DELETE FROM organizations WHERE id = ?').bind('org-default').run()
      if (defaultOrganization) {
        await env.DB.prepare('INSERT INTO organizations (id, name, owner_uid, setup_completed) VALUES (?, ?, ?, ?)')
          .bind(defaultOrganization.id, defaultOrganization.name, defaultOrganization.owner_uid, defaultOrganization.setup_completed)
          .run()
      }
    }
  })
})
