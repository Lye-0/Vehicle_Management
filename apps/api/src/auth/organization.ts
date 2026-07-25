import { and, asc, eq } from 'drizzle-orm'
import { authAccounts, organizationMemberships, organizations, staffProfiles } from '@vehicle-management/database'
import { requireAuthenticatedUser, type FirebaseUser } from './firebase'
import { HttpError } from '../http'
import type { Database } from '../db/client'

export const defaultOrganizationId = 'org-default'
export const organizationRoles = ['owner', 'admin', 'employee'] as const
export type OrganizationRole = typeof organizationRoles[number]

export type OrganizationMembership = {
  id: string
  organizationId: string
  name: string
  role: OrganizationRole
  status: string
}

export type OrganizationContext = {
  user: FirebaseUser
  organization: OrganizationMembership
}

export async function requireOrganizationContext(request: Request, env: Env, database: Database): Promise<OrganizationContext> {
  const user = await requireAuthenticatedUser(request, env)
  await ensureDevelopmentMembership(database, env, user)
  const organizationId = request.headers.get('X-Organization-Id')?.trim() || new URL(request.url).searchParams.get('organizationId')?.trim() || ''
  const memberships = await loadMemberships(database, user.uid)
  const organization = organizationId
    ? memberships.find((membership) => membership.organizationId === organizationId)
    : memberships.length === 1 ? memberships[0] : undefined
  if (!organization) {
    if (!memberships.length) throw new HttpError(403, '利用できる組織がありません。管理者に登録を依頼してください。')
    throw new HttpError(400, '利用する組織を選択してください。')
  }
  if (organization.status !== 'active') throw new HttpError(403, 'この組織の利用権限が無効です。')
  return { user, organization }
}

export async function requireAdminOrganizationContext(request: Request, env: Env, database: Database): Promise<OrganizationContext> {
  const context = await requireOrganizationContext(request, env, database)
  if (context.organization.role !== 'owner' && context.organization.role !== 'admin') {
    throw new HttpError(403, 'この操作には管理者権限が必要です。')
  }
  return context
}

export async function loadMemberships(database: Database, uid: string): Promise<OrganizationMembership[]> {
  const [membershipRows, organizationRows] = await Promise.all([
    database.select().from(organizationMemberships).where(eq(organizationMemberships.uid, uid)).orderBy(asc(organizationMemberships.createdAt)).all(),
    database.select().from(organizations).orderBy(asc(organizations.createdAt)).all(),
  ])
  const organizationsById = new Map(organizationRows.map((organization) => [organization.id, organization]))
  return membershipRows.flatMap((membership) => {
    const organization = organizationsById.get(membership.organizationId)
    if (!organization) return []
    return [{
      id: membership.id,
      organizationId: organization.id,
      name: organization.name,
      role: normalizeRole(membership.role),
      status: membership.status,
    }]
  })
}

export async function loadAuthSession(database: Database, env: Env, user: FirebaseUser) {
  await ensureDevelopmentMembership(database, env, user)
  const [memberships, account, profile, incompleteOrganization] = await Promise.all([
    loadMemberships(database, user.uid),
    database.select().from(authAccounts).where(eq(authAccounts.uid, user.uid)).get(),
    database.select().from(staffProfiles).where(eq(staffProfiles.uid, user.uid)).get(),
    database.select({ id: organizations.id }).from(organizations).where(eq(organizations.setupCompleted, false)).get(),
  ])
  return {
    user,
    profile: profile ? { displayName: profile.displayName, email: profile.email, role: profile.role } : null,
    organizations: memberships,
    setupAvailable: Boolean(incompleteOrganization) && memberships.length === 0,
    mustChangePassword: account?.mustChangePassword ?? false,
  }
}

export async function completeInitialOrganizationSetup(database: Database, env: Env, user: FirebaseUser, name: string, setupKey: string) {
  const normalizedName = name.trim()
  if (!normalizedName) throw new HttpError(400, '組織名を入力してください。')
  if (!(env.APP_ENV === 'development' && env.FIREBASE_AUTH_EMULATOR === 'true')) {
    if (!env.INITIAL_SETUP_KEY || setupKey !== env.INITIAL_SETUP_KEY) throw new HttpError(403, '初回セットアップキーが正しくありません。')
  }

  const target = await database.select().from(organizations).where(eq(organizations.setupCompleted, false)).orderBy(asc(organizations.createdAt)).get()
  if (!target) throw new HttpError(409, '初回セットアップはすでに完了しています。')

  const existingMembership = await database.select({ id: organizationMemberships.id }).from(organizationMemberships).where(and(eq(organizationMemberships.organizationId, target.id), eq(organizationMemberships.uid, user.uid))).get()
  const now = new Date().toISOString()
  await database.update(organizations).set({ name: normalizedName, ownerUid: user.uid, setupCompleted: true, updatedAt: now }).where(and(eq(organizations.id, target.id), eq(organizations.setupCompleted, false))).run()
  if (!existingMembership) {
    await database.insert(organizationMemberships).values({ id: crypto.randomUUID(), organizationId: target.id, uid: user.uid, role: 'owner', status: 'active', updatedAt: now }).run()
  }
  await upsertProfile(database, user, 'owner')
  await ensureAuthAccount(database, user.uid)
  return target.id
}

export async function completeInitialPasswordChange(database: Database, uid: string) {
  const account = await database.select({ uid: authAccounts.uid, mustChangePassword: authAccounts.mustChangePassword }).from(authAccounts).where(eq(authAccounts.uid, uid)).get()
  if (!account) throw new HttpError(404, '認証アカウント情報が見つかりません。')
  if (!account.mustChangePassword) return
  await database.update(authAccounts).set({ mustChangePassword: false, initialPasswordChangedAt: new Date().toISOString(), updatedAt: new Date().toISOString() }).where(eq(authAccounts.uid, uid)).run()
}

export async function ensureDevelopmentMembership(database: Database, env: Env, user: FirebaseUser) {
  if (!(env.APP_ENV === 'development' && env.FIREBASE_AUTH_EMULATOR === 'true')) return
  const organization = await database.select({ id: organizations.id }).from(organizations).where(eq(organizations.id, defaultOrganizationId)).get()
  if (!organization) return
  const existingMembership = await database.select({ id: organizationMemberships.id, role: organizationMemberships.role }).from(organizationMemberships).where(and(eq(organizationMemberships.organizationId, defaultOrganizationId), eq(organizationMemberships.uid, user.uid))).get()
  const now = new Date().toISOString()
  if (!existingMembership) {
    await database.insert(organizationMemberships).values({ id: crypto.randomUUID(), organizationId: defaultOrganizationId, uid: user.uid, role: 'owner', status: 'active', updatedAt: now }).run()
  }
  await upsertProfile(database, user, existingMembership ? normalizeRole(existingMembership.role) : 'owner')
  await ensureAuthAccount(database, user.uid)
}

async function upsertProfile(database: Database, user: FirebaseUser, role: OrganizationRole) {
  const existing = await database.select({ uid: staffProfiles.uid }).from(staffProfiles).where(eq(staffProfiles.uid, user.uid)).get()
  const now = new Date().toISOString()
  const displayName = user.displayName || user.email || 'ログインユーザー'
  if (existing) {
    await database.update(staffProfiles).set({ displayName, email: user.email, role, updatedAt: now }).where(eq(staffProfiles.uid, user.uid)).run()
  } else {
    await database.insert(staffProfiles).values({ uid: user.uid, displayName, email: user.email, role, updatedAt: now }).run()
  }
}

async function ensureAuthAccount(database: Database, uid: string) {
  const existing = await database.select({ uid: authAccounts.uid }).from(authAccounts).where(eq(authAccounts.uid, uid)).get()
  if (!existing) await database.insert(authAccounts).values({ uid }).run()
}

function normalizeRole(value: string): OrganizationRole {
  return organizationRoles.includes(value as OrganizationRole) ? value as OrganizationRole : 'employee'
}
