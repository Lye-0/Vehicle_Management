import { AwsClient } from 'aws4fetch'

export type B2ObjectInput = {
  key: string
  body: ArrayBuffer
  contentType: string
}

export function createB2Storage(env: Env) {
  const config = getB2Config(env)
  const client = new AwsClient({
    accessKeyId: config.keyId,
    secretAccessKey: config.applicationKey,
    service: 's3',
    region: config.region,
  })
  const bucketUrl = `${config.endpoint}/${encodeURIComponent(config.bucket)}`

  return {
    async putObject({ key, body, contentType }: B2ObjectInput) {
      const response = await client.fetch(`${bucketUrl}/${encodeObjectKey(key)}`, {
        method: 'PUT',
        headers: { 'content-type': contentType },
        body,
      })
      await assertSuccessfulResponse(response, 'B2へのファイル保存に失敗しました。')
    },

    async deleteObject(key: string) {
      const response = await client.fetch(`${bucketUrl}/${encodeObjectKey(key)}`, { method: 'DELETE' })
      await assertSuccessfulResponse(response, 'B2のファイル削除に失敗しました。')
    },

    async getObject(key: string) {
      const response = await client.fetch(`${bucketUrl}/${encodeObjectKey(key)}`, { method: 'GET' })
      if (!response.ok) {
        const detail = await response.text()
        throw new Error(`B2のファイル取得に失敗しました。 ${detail.slice(0, 200)}`)
      }
      return response
    },

    objectUrl(key: string) {
      return `${bucketUrl}/${encodeObjectKey(key)}`
    },
  }
}

function getB2Config(env: Env) {
  const values = {
    endpoint: env.B2_ENDPOINT?.replace(/\/$/, ''),
    region: env.B2_REGION,
    bucket: env.B2_BUCKET,
    keyId: env.B2_KEY_ID,
    applicationKey: env.B2_APPLICATION_KEY,
  }
  if (Object.values(values).some((value) => !value)) throw new Error('B2の環境変数が不足しています。')
  return values as { endpoint: string; region: string; bucket: string; keyId: string; applicationKey: string }
}

function encodeObjectKey(key: string) {
  return key.split('/').map((segment) => encodeURIComponent(segment)).join('/')
}

async function assertSuccessfulResponse(response: Response, message: string) {
  if (response.ok) return
  const detail = await response.text()
  throw new Error(`${message} ${detail.slice(0, 200)}`)
}
