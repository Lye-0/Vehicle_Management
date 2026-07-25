import { defineConfig } from 'drizzle-kit'

export default defineConfig({
  dialect: 'sqlite',
  schema: '../../packages/database/src/schema.ts',
  out: './migrations',
  strict: true,
  verbose: true,
})
