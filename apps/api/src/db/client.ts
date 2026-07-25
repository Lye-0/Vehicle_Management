import { drizzle } from 'drizzle-orm/d1'
import { databaseSchema } from '@vehicle-management/database'

export function createDatabase(database: D1Database) {
  return drizzle(database, { schema: databaseSchema })
}

export type Database = ReturnType<typeof createDatabase>
