import { and, asc, eq } from 'drizzle-orm'
import { authAccounts, organizationMemberships, staffProfiles } from '@vehicle-management/database'
import { IdentityToolkitError, createEmailPasswordUser, deleteEmailPasswordUser, sendPasswordResetEmail } from '../auth/identity-toolkit'
import { requireAdminOrganizationContext, requireOrganizationContext, type OrganizationRole } from '../auth/organization'
import { UnauthorizedError } from '../auth/firebase'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'

const memberRoles = ['owner', 'admin', 'employee'] as const
const memberStatuses = ['active', 'suspended'] as const
type MemberRole = typeof memberRoles[number]
type MemberStatus = typeof memberStatuses[number]

export async function handleMemberRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const collectionPath = pathname === '/api/organization/members'
  const resetMatch = pathname.match(/^\/api\/organization\/members\/([^/]+)\/password-reset$/)
  const memberMatch = pathname.match(/^\/api\/organization\/members\/([^/]+)$/)
  if (!collectionPath && !resetMatch && !memberMatch) return null

  try {
    const database = createDatabase(env.DB)
    if (collectionPath && request.method === 'GET') {
      const context = await requireOrganizationContext(request, env, database)
      return jsonResponse({ currentRole: context.organization.role, members: await listMembers(database, context.organization.organizationId, context.user.uid) }, 200, env)
    }
    if (collectionPath && request.method === 'POST') return createMember(request, env, database)
    if (resetMatch && request.method === 'POST') return resetMemberPassword(request, env, database, decodeURIComponent(resetMatch[1]))
    if (memberMatch && request.method === 'PATCH') return updateMember(request, env, database, decodeURIComponent(memberMatch[1]))
    throw new HttpError(405, 'この操作には対応していません。')
  } catch (error) {
    if (error instanceof UnauthorizedError) return jsonResponse({ error: error.message }, 401, env)
    if (error instanceof HttpError) return jsonResponse({ error: error.message }, error.status, env)
    console.error(error)
    return jsonResponse({ error: '管理者・従業員情報の処理に失敗しました。' }, 500, env)
  }
}

async function createMember(request: Request, env: Env, database: ReturnType<typeof createDatabase>) {
  const context = await requireAdminOrganizationContext(request, env, database)
  const body = await readJson(request)
  const displayName = requiredDisplayName(body.displayName)
  const email = requiredEmail(body.email)
  const temporaryPassword = createTemporaryPassword()

  let createdUser: { uid: string; idToken: string } | null = null
  const membershipId = crypto.randomUUID()
  try {
    createdUser = await createEmailPasswordUser(env, email, temporaryPassword)
    const now = new Date().toISOString()
    await database.insert(organizationMemberships).values({
      id: membershipId,
      organizationId: context.organization.organizationId,
      uid: createdUser.uid,
      role: 'employee',
      status: 'active',
      updatedAt: now,
    }).run()
    await database.insert(staffProfiles).values({ uid: createdUser.uid, displayName, email, role: 'employee', updatedAt: now }).run()
    await database.insert(authAccounts).values({ uid: createdUser.uid, mustChangePassword: true, initialPasswordIssuedAt: now, updatedAt: now }).run()
  } catch (error) {
    if (error instanceof IdentityToolkitError && error.code === 'EMAIL_EXISTS') {
      throw new HttpError(409, 'そのメールアドレスは既に登録されています。既存アカウントの所属追加は次の段階で対応します。')
    }
    if (createdUser) {
      try {
        await database.delete(authAccounts).where(eq(authAccounts.uid, createdUser.uid)).run()
        await database.delete(staffProfiles).where(eq(staffProfiles.uid, createdUser.uid)).run()
        await database.delete(organizationMemberships).where(eq(organizationMemberships.id, membershipId)).run()
      } catch (databaseCleanupError) {
        console.error(databaseCleanupError)
      }
      try {
        await deleteEmailPasswordUser(env, createdUser.idToken)
      } catch (cleanupError) {
        console.error(cleanupError)
      }
    }
    throw error
  }

  const members = await listMembers(database, context.organization.organizationId, context.user.uid)
  const member = members.find((item) => item.uid === createdUser!.uid)
  return jsonResponse({ member, temporaryPassword }, 201, env)
}

async function updateMember(request: Request, env: Env, database: ReturnType<typeof createDatabase>, uid: string) {
  const context = await requireAdminOrganizationContext(request, env, database)
  const target = await database.select().from(organizationMemberships).where(and(eq(organizationMemberships.organizationId, context.organization.organizationId), eq(organizationMemberships.uid, uid))).get()
  if (!target) throw new HttpError(404, '対象ユーザーが見つかりません。')
  const targetRole = normalizeRole(target.role)
  if (targetRole === 'owner') throw new HttpError(403, 'オーナーの所属情報は変更できません。')
  if (context.organization.role === 'admin' && targetRole !== 'employee') throw new HttpError(403, '管理者は他の管理者の所属情報を変更できません。')

  const body = await readJson(request)
  const nextRole = body.role === undefined ? targetRole : requestedRole(body.role)
  const nextStatus = body.status === undefined ? normalizeStatus(target.status) : requestedStatus(body.status)
  const displayName = body.displayName === undefined ? null : requiredDisplayName(body.displayName)
  if (nextRole === 'owner') throw new HttpError(400, 'オーナーへの変更はこの画面では対応していません。')
  if (uid === context.user.uid && nextStatus === 'suspended') throw new HttpError(400, '自分自身を利用停止にはできません。')

  const memberships = await database.select().from(organizationMemberships).where(eq(organizationMemberships.organizationId, context.organization.organizationId)).all()
  const activeAdministrators = memberships.filter((membership) => normalizeStatus(membership.status) === 'active' && isAdministrator(normalizeRole(membership.role)))
  if (isAdministrator(targetRole) && (nextStatus !== 'active' || !isAdministrator(nextRole)) && activeAdministrators.length <= 1) {
    throw new HttpError(400, '組織には少なくとも1人の有効な管理者が必要です。')
  }

  const now = new Date().toISOString()
  await database.update(organizationMemberships).set({ role: nextRole, status: nextStatus, updatedAt: now }).where(and(eq(organizationMemberships.organizationId, context.organization.organizationId), eq(organizationMemberships.uid, uid))).run()
  if (displayName !== null) await database.update(staffProfiles).set({ displayName, updatedAt: now }).where(eq(staffProfiles.uid, uid)).run()
  return jsonResponse({ members: await listMembers(database, context.organization.organizationId, context.user.uid) }, 200, env)
}

async function resetMemberPassword(request: Request, env: Env, database: ReturnType<typeof createDatabase>, uid: string) {
  const context = await requireAdminOrganizationContext(request, env, database)
  const target = await database.select().from(organizationMemberships).where(and(eq(organizationMemberships.organizationId, context.organization.organizationId), eq(organizationMemberships.uid, uid))).get()
  if (!target) throw new HttpError(404, '対象ユーザーが見つかりません。')
  const targetRole = normalizeRole(target.role)
  if (targetRole === 'owner' || (context.organization.role === 'admin' && targetRole !== 'employee')) throw new HttpError(403, 'このユーザーのパスワード再設定は実行できません。')
  const profile = await database.select().from(staffProfiles).where(eq(staffProfiles.uid, uid)).get()
  if (!profile?.email) throw new HttpError(400, '対象ユーザーにメールアドレスが登録されていません。')
  try {
    await sendPasswordResetEmail(env, profile.email)
  } catch (error) {
    if (error instanceof IdentityToolkitError && error.code === 'EMAIL_NOT_FOUND') throw new HttpError(404, 'メール認証アカウントが見つかりません。')
    throw error
  }
  return jsonResponse({ message: 'パスワード再設定メールを送信しました。' }, 200, env)
}

async function listMembers(database: ReturnType<typeof createDatabase>, organizationId: string, currentUid: string) {
  const [membershipRows, profileRows, accountRows] = await Promise.all([
    database.select().from(organizationMemberships).where(eq(organizationMemberships.organizationId, organizationId)).orderBy(asc(organizationMemberships.createdAt)).all(),
    database.select().from(staffProfiles).all(),
    database.select().from(authAccounts).all(),
  ])
  const profilesByUid = new Map(profileRows.map((profile) => [profile.uid, profile]))
  const accountsByUid = new Map(accountRows.map((account) => [account.uid, account]))
  return membershipRows.map((membership) => {
    const profile = profilesByUid.get(membership.uid)
    const account = accountsByUid.get(membership.uid)
    return {
      uid: membership.uid,
      displayName: profile?.displayName ?? '未設定ユーザー',
      email: profile?.email ?? null,
      role: normalizeRole(membership.role),
      status: normalizeStatus(membership.status),
      mustChangePassword: account?.mustChangePassword ?? false,
      isSelf: membership.uid === currentUid,
    }
  })
}

function requiredDisplayName(value: unknown) {
  const displayName = typeof value === 'string' ? value.trim().slice(0, 100) : ''
  if (!displayName) throw new HttpError(400, '表示名を入力してください。')
  return displayName
}

function requiredEmail(value: unknown) {
  const email = typeof value === 'string' ? value.trim().toLowerCase() : ''
  if (!email || email.length > 254 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) throw new HttpError(400, '有効なメールアドレスを入力してください。')
  return email
}

function requestedRole(value: unknown): MemberRole {
  if (typeof value !== 'string' || !memberRoles.includes(value as MemberRole) || value === 'owner') throw new HttpError(400, '変更できる権限は管理者または従業員です。')
  return value as MemberRole
}

function requestedStatus(value: unknown): MemberStatus {
  if (typeof value !== 'string' || !memberStatuses.includes(value as MemberStatus)) throw new HttpError(400, '利用状態が不正です。')
  return value as MemberStatus
}

function normalizeRole(value: string): MemberRole {
  return memberRoles.includes(value as MemberRole) ? value as MemberRole : 'employee'
}

function normalizeStatus(value: string): MemberStatus {
  return memberStatuses.includes(value as MemberStatus) ? value as MemberStatus : 'active'
}

function isAdministrator(role: OrganizationRole) {
  return role === 'owner' || role === 'admin'
}

function createTemporaryPassword() {
  const required = [randomCharacter('ABCDEFGHJKLMNPQRSTUVWXYZ'), randomCharacter('abcdefghijkmnopqrstuvwxyz'), randomCharacter('23456789'), randomCharacter('!@#$%^&*')]
  const characters = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*'
  const password = [...required]
  while (password.length < 16) password.push(randomCharacter(characters))
  for (let index = password.length - 1; index > 0; index -= 1) {
    const swapIndex = randomNumber(index + 1)
    ;[password[index], password[swapIndex]] = [password[swapIndex], password[index]]
  }
  return password.join('')
}

function randomCharacter(characters: string) {
  return characters[randomNumber(characters.length)]
}

function randomNumber(maximum: number) {
  const values = new Uint32Array(1)
  crypto.getRandomValues(values)
  return values[0] % maximum
}
