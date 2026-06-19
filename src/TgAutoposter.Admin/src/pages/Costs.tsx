import { useAppData } from '../lib/appData'
import { money } from '../lib/format'
import { Card, PageHead, Stat } from '../components/ui'

export default function Costs() {
  const { dashboard } = useAppData()
  const d = dashboard

  return (
    <>
      <PageHead title="Аналитика расходов" subtitle="Расходы на AI и стоимость публикаций" />

      <div className="grid cols-3">
        <Stat label="Расход сегодня" value={d ? money(d.aiSpendToday) : '—'} />
        <Stat label="Расход за месяц" value={d ? money(d.aiSpendMonth) : '—'} accent />
        <Stat label="Polza факт сегодня" value={d ? money(d.providerSpendToday) : '—'} />
        <Stat label="Polza факт за месяц" value={d ? money(d.providerSpendMonth) : '—'} />
        <Stat label="Средняя цена поста" value={d ? money(d.averagePublishedPostCost) : '—'} />
        <Stat label="Опубликовано за месяц" value={d ? d.publishedMonth : '—'} />
      </div>

      <Card title="Как считаются расходы" className="pad-lg">
        <div className="faint" style={{ fontSize: 12.5 }}>
          «Расход» — это расчётная оценка по тарифам моделей: стоимость генерации текста и
          изображений, посчитанная по количеству токенов и прайс-листу провайдера.
          «Polza факт» — фактические данные о списаниях, полученные напрямую от провайдера
          Polza.ai, поэтому эти суммы могут отличаться от расчётной оценки.
        </div>
      </Card>
    </>
  )
}
