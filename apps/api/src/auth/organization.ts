import { and, asc, eq } from 'drizzle-orm'
import { authAccounts, organizationMemberships, organizations, staffProfiles } from '@vehicle-management/database'
import { requireAuthenticatedUser, type FirebaseUser } from './firebase'
import { IdentityToolkitError, updatePasswordWithIdToken } from './identity-toolkit'
import { HttpError } from '../http'
import type { Database } from '../db/client'
import { loadOrganizationPermissions, type OrganizationPermissions } from '../organization-permissions'

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
  const memberships = isDevelopmentAnonymousUser(env, user) ? await loadDevelopmentAnonymousMembership(database) : await loadMemberships(database, user.uid)
  const organization = organizationId
    ? memberships.find((membership) => membership.organizationId === organizationId)
    : memberships.length === 1 ? memberships[0] : undefined
  if (!organization) {
    if (!memberships.length) throw new HttpError(403, '利用できる組織がありません。管理者に登録を依頼してください。')
    throw new HttpError(400, '利用する組織を選択してください。')
  }
  if (organization.status !== 'active') throw new HttpError(403, 'この組織の利用権限が無効です。')
  const account = await database.select({ mustChangePassword: authAccounts.mustChangePassword }).from(authAccounts).where(eq(authAccounts.uid, user.uid)).get()
  if (account?.mustChangePassword) throw new HttpError(403, '初期パスワードの変更が必要です。')
  return { user, organization }
}

export async function requireAdminOrganizationContext(request: Request, env: Env, database: Database): Promise<OrganizationContext> {
  const context = await requireOrganizationContext(request, env, database)
  if (context.organization.role !== 'owner' && context.organization.role !== 'admin') {
    throw new HttpError(403, 'この操作には管理者権限が必要です。')
  }
  return context
}

export async function requireOrganizationPermission(request: Request, env: Env, database: Database, permission: keyof OrganizationPermissions): Promise<OrganizationContext> {
  const context = await requireOrganizationContext(request, env, database)
  if (context.organization.role === 'owner' || context.organization.role === 'admin') return context
  const permissions = await loadOrganizationPermissions(database, context.organization.organizationId)
  if (!permissions[permission]) throw new HttpError(403, 'この操作は組織の権限設定で許可されていません。')
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

function isDevelopmentAnonymousUser(env: Pick<Env, 'APP_ENV' | 'FIREBASE_AUTH_EMULATOR'>, user: FirebaseUser) {
  return env.APP_ENV === 'development' && env.FIREBASE_AUTH_EMULATOR === 'true' && user.isAnonymous
}

async function loadDevelopmentAnonymousMembership(database: Database): Promise<OrganizationMembership[]> {
  const organization = await database.select().from(organizations).where(eq(organizations.id, defaultOrganizationId)).get()
  if (!organization) return []
  return [{
    id: 'development-anonymous-' + organization.id,
    organizationId: organization.id,
    name: organization.name,
    role: 'owner',
    status: 'active',
  }]
}
export async function loadAuthSession(database: Database, env: Env, user: FirebaseUser) {
  await ensureDevelopmentMembership(database, env, user)
  const membershipsPromise = isDevelopmentAnonymousUser(env, user) ? loadDevelopmentAnonymousMembership(database) : loadMemberships(database, user.uid)
  const [memberships, account, profile, incompleteOrganization] = await Promise.all([
    membershipsPromise,
    database.select().from(authAccounts).where(eq(authAccounts.uid, user.uid)).get(),
    database.select().from(staffProfiles).where(eq(staffProfiles.uid, user.uid)).get(),
    database.select({ id: organizations.id }).from(organizations).where(eq(organizations.setupCompleted, false)).get(),
  ])
  return {
    user,
    profile: profile ? { displayName: profile.displayName, email: profile.email, role: profile.role } : user.isAnonymous ? { displayName: user.displayName || '開発用アカウント', email: user.email, role: 'owner' } : null,
    organizations: memberships,
    setupAvailable: !user.isAnonymous && Boolean(incompleteOrganization) && memberships.length === 0,
    mustChangePassword: account?.mustChangePassword ?? false,
  }
}

export async function completeInitialOrganizationSetup(database: Database, env: Env, user: FirebaseUser, name: string, setupKey: string) {
  const normalizedName = name.trim()
  if (!normalizedName) throw new HttpError(400, '組織名を入力してください。')
  if (user.isAnonymous) throw new HttpError(403, '開発用匿名ログインでは組織セットアップを実行できません。')
  if (!(env.APP_ENV === 'development' && env.FIREBASE_AUTH_EMULATOR === 'true')) {
    if (!env.INITIAL_SETUP_KEY || setupKey !== env.INITIAL_SETUP_KEY) throw new HttpError(403, '初回セットアップキーが正しくありません。')
  }

  const target = await database.select().from(organizations).where(eq(organizations.setupCompleted, false)).orderBy(asc(organizations.createdAt)).get()
  if (!target) throw new HttpError(409, '初回セットアップはすでに完了しています。')

  const now = new Date().toISOString()
  const updated = await database.update(organizations).set({ name: normalizedName, ownerUid: user.uid, setupCompleted: true, updatedAt: now }).where(and(eq(organizations.id, target.id), eq(organizations.setupCompleted, false))).run()
  if (updated.meta.changes !== 1) throw new HttpError(409, '初回セットアップは別のユーザーによって完了しました。')
  const existingMembership = await database.select({ id: organizationMemberships.id }).from(organizationMemberships).where(and(eq(organizationMemberships.organizationId, target.id), eq(organizationMemberships.uid, user.uid))).get()
  if (!existingMembership) {
    await database.insert(organizationMemberships).values({ id: crypto.randomUUID(), organizationId: target.id, uid: user.uid, role: 'owner', status: 'active', updatedAt: now }).run()
  }
  await upsertProfile(database, user, 'owner')
  await ensureAuthAccount(database, user.uid)
  return target.id
}

export async function completeInitialPasswordChange(database: Database, env: Env, uid: string, idToken: string, password: string) {
  const account = await database.select({ uid: authAccounts.uid, mustChangePassword: authAccounts.mustChangePassword }).from(authAccounts).where(eq(authAccounts.uid, uid)).get()
  if (!account) throw new HttpError(404, '認証アカウント情報が見つかりません。')
  if (!account.mustChangePassword) return
  if (!idToken) throw new HttpError(401, '認証トークンが見つかりません。')
  try {
    await updatePasswordWithIdToken(env, idToken, password)
  } catch (error) {
    if (error instanceof IdentityToolkitError && error.code === 'WEAK_PASSWORD') throw new HttpError(400, 'パスワードは8文字以上で設定してください。')
    if (error instanceof IdentityToolkitError && ['INVALID_ID_TOKEN', 'TOKEN_EXPIRED', 'USER_NOT_FOUND'].includes(error.code)) throw new HttpError(401, '認証情報を確認できません。')
    throw new HttpError(503, 'パスワードを変更できませんでした。Firebaseの設定を確認してください。')
  }
  const now = new Date().toISOString()
  await database.update(authAccounts).set({ mustChangePassword: false, initialPasswordChangedAt: now, updatedAt: now }).where(and(eq(authAccounts.uid, uid), eq(authAccounts.mustChangePassword, true))).run()
}

export async function ensureDevelopmentMembership(database: Database, env: Env, user: FirebaseUser) {
  if (!(env.APP_ENV === 'development' && env.FIREBASE_AUTH_EMULATOR === 'true')) return
  if (user.isAnonymous) return
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
  const existing = await database.select({ uid: staffProfiles.uid, displayName: staffProfiles.displayName, email: staffProfiles.email }).from(staffProfiles).where(eq(staffProfiles.uid, user.uid)).get()
  const now = new Date().toISOString()
  const displayName = user.displayName || user.email || 'ログインユーザー'
  const authenticatedEmail = user.email?.trim().toLowerCase() ?? null
  if (existing) {
    await database.update(staffProfiles).set({ displayName: existing.displayName || displayName, email: authenticatedEmail ?? existing.email, role, updatedAt: now }).where(eq(staffProfiles.uid, user.uid)).run()
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
