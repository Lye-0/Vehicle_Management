import { useId, type CSSProperties, type ReactNode } from 'react'

type IconWithChainProps = {
  children: ReactNode
  visible?: boolean
  chainWidth?: string
  chainTop?: string
  chainDepth?: number
  linkThickness?: number
  linkSize?: number
  gold?: string
  shadow?: string
  className?: string
}

export function IconWithChain({
  children,
  visible = true,
  chainWidth = '155%',
  chainTop = '-24%',
  chainDepth = 28,
  linkThickness = 4.5,
  linkSize = 13,
  gold = '#d99b18',
  shadow = '0 2px 2px rgba(82, 50, 0, 0.38)',
  className = '',
}: IconWithChainProps) {
  const id = useId().replace(/[^a-z0-9_-]/gi, '')
  const gradientId = `icon-chain-gradient-${id}`
  const filterId = `icon-chain-shadow-${id}`
  const depth = Math.max(18, Math.min(42, chainDepth))
  const centerY = 10 + depth
  const innerY = 10 + depth * 0.58
  const size = Math.max(4, Math.min(18, linkSize))
  const rx = size
  const ry = size * 0.48
  const style = {
    '--icon-chain-width': chainWidth,
    '--icon-chain-top': chainTop,
    '--icon-chain-thickness': `${Math.max(2, linkThickness)}px`,
    '--icon-chain-gold': gold,
    '--icon-chain-shadow': shadow,
  } as CSSProperties

  return <span className={`icon-with-chain ${className}`.trim()} style={style}>
    <span className="icon-with-chain-content">{children}</span>
    {visible && <svg className="icon-with-chain-svg" viewBox="0 0 120 62" preserveAspectRatio="none" role="presentation" aria-hidden="true">
      <defs>
        <linearGradient id={gradientId} x1="0" y1="0" x2="0.1" y2="1">
          <stop offset="0%" stopColor="#fff4a3" />
          <stop offset="26%" stopColor="#f7cf4b" />
          <stop offset="54%" stopColor="var(--icon-chain-gold)" />
          <stop offset="78%" stopColor="#ffe37a" />
          <stop offset="100%" stopColor="#9a5b00" />
        </linearGradient>
        <filter id={filterId} x="-25%" y="-35%" width="150%" height="175%">
          <feDropShadow dx="0" dy="1.5" stdDeviation="1.1" floodColor="#6e4500" floodOpacity="0.5" />
        </filter>
      </defs>
      <g className="icon-with-chain-links" filter={`url(#${filterId})`} stroke={`url(#${gradientId})`}>
        <ellipse cx="8" cy="9" rx={rx} ry={ry} transform="rotate(38 8 9)" />
        <ellipse cx="29" cy={innerY} rx={rx} ry={ry} transform={`rotate(32 29 ${innerY})`} />
        <ellipse cx="60" cy={centerY} rx={rx + 1} ry={ry} />
        <ellipse cx="91" cy={innerY} rx={rx} ry={ry} transform={`rotate(-32 91 ${innerY})`} />
        <ellipse cx="112" cy="9" rx={rx} ry={ry} transform="rotate(-38 112 9)" />
      </g>
    </svg>}
  </span>
}
