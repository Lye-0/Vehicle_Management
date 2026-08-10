import { and, asc, eq } from 'drizzle-orm'
import { authAccounts, organizationInvites, organizationMemberships, staffProfiles } from '@vehicle-management/database'
import { IdentityToolkitError, createEmailPasswordUser, deleteEmailPasswordUser } from '../auth/identity-toolkit'
import { requireAdminOrganizationContext, requireOrganizationContext, type OrganizationRole } from '../auth/organization'
import { requireAuthenticatedUser, UnauthorizedError } from '../auth/firebase'
import { createDatabase } from '../db/client'
import { HttpError, jsonResponse, readJson } from '../http'

const memberRoles = ['owner', 'admin', 'employee'] as const
const memberStatuses = ['active', 'suspended'] as const
type MemberRole = typeof memberRoles[number]
type MemberStatus = typeof memberStatuses[number]

export async function handleMemberRoutes(request: Request, env: Env): Promise<Response | null> {
  const pathname = new URL(request.url).pathname.replace(/\/$/, '') || '/'
  const collectionPath = pathname === '/api/organization/members'
  const invitationAcceptPath = pathname === '/api/organization/invitations/accept'
  const memberMatch = pathname.match(/^\/api\/organization\/members\/([^/]+)$/)
  if (!collectionPath && !memberMatch && !invitationAcceptPath) return null

  try {
    const database = createDatabase(env.DB)
    if (invitationAcceptPath && request.method === 'POST') return await acceptInvitation(request, env, database)
    if (collectionPath && request.method === 'GET') {
      const context = await requireOrganizationContext(request, env, database)
      return jsonResponse({ currentRole: context.organization.role, members: await listMembers(database, context.organization.organizationId, context.user.uid) }, 200, env)
    }
    if (collectionPath && request.method === 'POST') return await createMember(request, env, database)
    if (memberMatch && request.method === 'PATCH') return await updateMember(request, env, database, decodeURIComponent(memberMatch[1]))
    if (memberMatch && request.method === 'DELETE') return await removeMember(request, env, database, decodeURIComponent(memberMatch[1]))
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
  const membershipId = crypto.randomUUID()
  let createdUser: { uid: string; idToken: string }
  try {
    createdUser = await createEmailPasswordUser(env, email, temporaryPassword)
  } catch (error) {
    if (error instanceof IdentityToolkitError && error.code === 'EMAIL_EXISTS') {
      const invitation = await createOrganizationInvitation(database, context.organization.organizationId, email, context.user.uid)
      return jsonResponse({ member: null, invitation }, 201, env)
    }
    throw error
  }

  const memberUid = createdUser.uid
  let membershipCreated = false
  try {
    const now = new Date().toISOString()
    await database.insert(organizationMemberships).values({
      id: membershipId,
      organizationId: context.organization.organizationId,
      uid: createdUser.uid,
      role: 'employee',
      status: 'active',
      updatedAt: now,
    }).run()
    membershipCreated = true
    await database.insert(staffProfiles).values({ uid: createdUser.uid, displayName, email, role: 'employee', updatedAt: now }).run()
    await database.insert(authAccounts).values({ uid: createdUser.uid, mustChangePassword: true, initialPasswordIssuedAt: now, updatedAt: now }).run()
  } catch (error) {
    if (membershipCreated) {
      try {
        await database.delete(organizationMemberships).where(eq(organizationMemberships.id, membershipId)).run()
      } catch (databaseCleanupError) {
        console.error(databaseCleanupError)
      }
    }
    if (createdUser) {
      try {
        await database.delete(authAccounts).where(eq(authAccounts.uid, createdUser.uid)).run()
        await database.delete(staffProfiles).where(eq(staffProfiles.uid, createdUser.uid)).run()
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
  const member = members.find((item) => item.uid === memberUid)
  return jsonResponse({ member, ...(temporaryPassword ? { temporaryPassword } : {}) }, 201, env)
}

async function acceptInvitation(request: Request, env: Env, database: ReturnType<typeof createDatabase>) {
  const user = await requireAuthenticatedUser(request, env)
  if (user.isAnonymous || !user.email || !user.emailVerified) throw new HttpError(403, '招待を受け入れるには、招待先メールアドレスでログインし、メール確認を完了してください。')
  const body = await readJson(request)
  const token = typeof body.code === 'string' ? body.code.trim() : ''
  if (!token || token.length > 200) throw new HttpError(400, '招待コードが不正です。')
  const tokenHash = await hashInvitationToken(token)
  const invite = await database.select().from(organizationInvites).where(and(eq(organizationInvites.tokenHash, tokenHash), eq(organizationInvites.status, 'pending'))).get()
  if (!invite) throw new HttpError(400, '招待コードが無効、使用済み、または期限切れです。')
  const now = new Date().toISOString()
  if (invite.expiresAt <= now) {
    await database.update(organizationInvites).set({ status: 'expired', updatedAt: now }).where(eq(organizationInvites.id, invite.id)).run()
    throw new HttpError(400, '招待コードの有効期限が切れています。')
  }
  if (normalizeEmail(user.email) !== invite.email) throw new HttpError(403, 'この招待コードは現在のログインアカウントには使用できません。')
  const existingMembership = await database.select({ id: organizationMemberships.id }).from(organizationMemberships).where(and(eq(organizationMemberships.organizationId, invite.organizationId), eq(organizationMemberships.uid, user.uid))).get()
  if (existingMembership) throw new HttpError(409, 'このユーザーはすでに組織に所属しています。')
  const claimed = await database.update(organizationInvites).set({ status: 'accepted', acceptedUid: user.uid, updatedAt: now }).where(and(eq(organizationInvites.id, invite.id), eq(organizationInvites.status, 'pending'), eq(organizationInvites.expiresAt, invite.expiresAt))).run()
  if (!claimed.meta.changes) throw new HttpError(409, '招待コードはすでに使用されています。')
  try {
    await database.insert(organizationMemberships).values({ id: crypto.randomUUID(), organizationId: invite.organizationId, uid: user.uid, role: 'employee', status: 'active', updatedAt: now }).run()
    const existingProfile = await database.select({ uid: staffProfiles.uid, displayName: staffProfiles.displayName }).from(staffProfiles).where(eq(staffProfiles.uid, user.uid)).get()
    const displayName = existingProfile?.displayName || user.displayName || user.email
    if (existingProfile) {
      await database.update(staffProfiles).set({ displayName, email: normalizeEmail(user.email), role: 'employee', updatedAt: now }).where(eq(staffProfiles.uid, user.uid)).run()
    } else {
      await database.insert(staffProfiles).values({ uid: user.uid, displayName, email: normalizeEmail(user.email), role: 'employee', updatedAt: now }).run()
    }
    const authAccount = await database.select({ uid: authAccounts.uid }).from(authAccounts).where(eq(authAccounts.uid, user.uid)).get()
    if (!authAccount) await database.insert(authAccounts).values({ uid: user.uid, mustChangePassword: false, updatedAt: now }).run()
  } catch (error) {
    console.error(error)
    throw new HttpError(409, '招待の受け入れに失敗しました。管理者へ再招待を依頼してください。')
  }
  const members = await listMembers(database, invite.organizationId, user.uid)
  return jsonResponse({ member: members.find((member) => member.uid === user.uid) }, 201, env)
}

async function createOrganizationInvitation(database: ReturnType<typeof createDatabase>, organizationId: string, email: string, createdByUid: string) {
  const now = new Date().toISOString()
  const expiresAt = new Date(Date.now() + 7 * 86_400_000).toISOString()
  await database.update(organizationInvites).set({ status: 'revoked', updatedAt: now }).where(and(eq(organizationInvites.organizationId, organizationId), eq(organizationInvites.email, email), eq(organizationInvites.status, 'pending'))).run()
  const code = createInvitationToken()
  await database.insert(organizationInvites).values({ id: crypto.randomUUID(), organizationId, email, tokenHash: await hashInvitationToken(code), role: 'employee', status: 'pending', expiresAt, createdByUid, updatedAt: now }).run()
  return { code, email, expiresAt }
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

async function removeMember(request: Request, env: Env, database: ReturnType<typeof createDatabase>, uid: string) {
  const context = await requireAdminOrganizationContext(request, env, database)
  const target = await database.select().from(organizationMemberships).where(and(eq(organizationMemberships.organizationId, context.organization.organizationId), eq(organizationMemberships.uid, uid))).get()
  if (!target) throw new HttpError(404, '対象ユーザーが見つかりません。')
  const targetRole = normalizeRole(target.role)
  if (targetRole === 'owner') throw new HttpError(403, 'オーナーは組織から削除できません。')
  if (uid === context.user.uid) throw new HttpError(400, '自分自身は組織から削除できません。')
  if (context.organization.role === 'admin' && targetRole !== 'employee') throw new HttpError(403, '管理者は他の管理者を組織から削除できません。')

  await database.delete(organizationMemberships).where(and(eq(organizationMemberships.organizationId, context.organization.organizationId), eq(organizationMemberships.uid, uid))).run()
  return jsonResponse({ members: await listMembers(database, context.organization.organizationId, context.user.uid) }, 200, env)
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

function createInvitationToken() {
  const bytes = new Uint8Array(24)
  crypto.getRandomValues(bytes)
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/u, '')
}

async function hashInvitationToken(value: string) {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value))
  const bytes = new Uint8Array(digest)
  let binary = ''
  for (const byte of bytes) binary += String.fromCharCode(byte)
  return btoa(binary)
}

function normalizeEmail(value: string) {
  return value.trim().toLowerCase()
}
