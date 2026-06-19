import { useCallback, useEffect, useState } from 'react'
import { ExternalLink } from 'lucide-react'
import { api } from '../lib/api'
import { useAppData } from '../lib/appData'
import { dateTime, kindLabels, money, statusLabels, statusTone } from '../lib/format'
import { Badge, Card, Empty, PageHead } from '../components/ui'
import type { PostItem } from '../lib/types'

export default function History() {
  const { selectedChannelId, live } = useAppData()
  const [posts, setPosts] = useState<PostItem[]>([])

  const load = useCallback(async () => {
    const all = await api.posts(undefined, selectedChannelId)
    const sorted = [...all].sort((a, b) => b.createdAtUtc.localeCompare(a.createdAtUtc))
    setPosts(sorted)
  }, [selectedChannelId])

  useEffect(() => { load() }, [load, live])

  return (
    <>
      <PageHead title="История публикаций" subtitle={`${posts.length} записей`} />
      <Card>
        {posts.length === 0 ? (
          <Empty>Пока нет записей.</Empty>
        ) : (
          <div className="table-wrap">
            <table className="tbl">
              <thead>
                <tr>
                  <th>Дата</th>
                  <th>Тип</th>
                  <th>Статус</th>
                  <th>Заголовок</th>
                  <th>Стоимость</th>
                  <th>Ссылки</th>
                </tr>
              </thead>
              <tbody>
                {posts.map((p) => (
                  <tr key={p.id}>
                    <td>{dateTime(p.createdAtUtc)}</td>
                    <td><Badge>{kindLabels[p.publicationKind] ?? p.publicationKind}</Badge></td>
                    <td><Badge tone={statusTone(p.status)}>{statusLabels[p.status]}</Badge></td>
                    <td>{p.sourceTitle}</td>
                    <td>{money(p.costAmount, p.costCurrency === 'RUB' ? '₽' : p.costCurrency)}</td>
                    <td>
                      <div className="row" style={{ gap: 8, alignItems: 'center' }}>
                        {p.sourceUrl && (
                          <a className="link" href={p.sourceUrl} target="_blank" rel="noreferrer">Источник</a>
                        )}
                        {p.telegramPostUrl && (
                          <a className="link" href={p.telegramPostUrl} target="_blank" rel="noreferrer">
                            <ExternalLink size={14} /> В канале
                          </a>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </>
  )
}
