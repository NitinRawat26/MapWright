import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { Icon } from '../core/icon';
import { Logo } from '../core/logo';

/**
 * The opening film: one processing-volume mapping between two acquiring systems, told scene by scene the way
 * the engine actually works — profile, detect, derive, map, AI for the gap, replay. Every number comes from the
 * shipped SalesAlpha → UW Core sample (samples/mappings/sales-alpha__uw-core).
 */
type Scene = 'systems' | 'profile' | 'detect' | 'derive' | 'mapping' | 'ai' | 'replay' | 'done';
const SCENES: { name: Scene; ms: number }[] = [
  { name: 'systems', ms: 4400 },
  { name: 'profile', ms: 5000 },
  { name: 'detect', ms: 5600 },
  { name: 'derive', ms: 6800 },
  { name: 'mapping', ms: 6400 },
  { name: 'ai', ms: 5200 },
  { name: 'replay', ms: 7200 },
  { name: 'done', ms: 0 },
];
const ORDER: Scene[] = SCENES.map((s) => s.name);

interface Pt {
  x: number;
  y: number;
}
interface FieldDef {
  id: string;
  side: 'src' | 'tgt';
  path: string;
  type: string;
  sample: string;
  concept?: string;
  qualifier?: string;
  /** Scene-relative delay (ms) for the chip to leave its system card. */
  delay: number;
}

const FIELDS: FieldDef[] = [
  {
    id: 's-vol',
    side: 'src',
    path: '$.processing.annualCardVolume',
    type: 'integer',
    sample: '***0000',
    concept: 'ProcessingVolume.CardVolume',
    qualifier: 'period = annual',
    delay: 0,
  },
  {
    id: 's-avg',
    side: 'src',
    path: '$.processing.averageTicket',
    type: 'number',
    sample: '**.1',
    concept: 'ProcessingVolume.AverageTicket',
    delay: 120,
  },
  {
    id: 's-cp',
    side: 'src',
    path: '$.processing.cardPresentPercent',
    type: 'integer',
    sample: '**',
    concept: 'ChannelMix.CardPresent',
    qualifier: 'unit = percent',
    delay: 240,
  },
  {
    id: 's-moto',
    side: 'src',
    path: '$.processing.motoPercent',
    type: 'integer',
    sample: '*',
    concept: 'ChannelMix.Moto',
    qualifier: 'unit = percent',
    delay: 360,
  },
  {
    id: 's-ecom',
    side: 'src',
    path: '$.processing.ecommPercent',
    type: 'integer',
    sample: '**',
    concept: 'ChannelMix.Ecommerce',
    qualifier: 'unit = percent',
    delay: 480,
  },
  {
    id: 't-vol',
    side: 'tgt',
    path: '/Processing/MonthlyVolume',
    type: 'decimal',
    sample: '****00.00',
    concept: 'ProcessingVolume.CardVolume',
    qualifier: 'period = monthly',
    delay: 60,
  },
  {
    id: 't-avg',
    side: 'tgt',
    path: '/Processing/AverageTicket',
    type: 'decimal',
    sample: '**.10',
    concept: 'ProcessingVolume.AverageTicket',
    delay: 180,
  },
  {
    id: 't-high',
    side: 'tgt',
    path: '/Processing/HighTicket',
    type: 'decimal',
    sample: '***.00',
    concept: 'ProcessingVolume.HighTicket',
    delay: 300,
  },
  {
    id: 't-cp',
    side: 'tgt',
    path: '/Processing/CardPresentPct',
    type: 'decimal',
    sample: '**',
    concept: 'ChannelMix.CardPresent',
    qualifier: 'unit = percent',
    delay: 420,
  },
  {
    id: 't-cnp',
    side: 'tgt',
    path: '/Processing/CardNotPresentPct',
    type: 'decimal',
    sample: '**',
    concept: 'ChannelMix.CardNotPresent',
    qualifier: 'unit = percent',
    delay: 540,
  },
];

interface RuleDef {
  id: string;
  playbook: string;
  title: string;
  expression: string;
  inputs: string[];
  output: string;
  /** Working shown while the rule computes, then the answer. */
  working: string;
  answer: string;
  y: number;
}
const RULES: RuleDef[] = [
  {
    id: 'VOL-PERIOD-01',
    playbook: 'domain/processing-volume@1.0.0',
    title: 'Annual volume to monthly',
    expression: 'annual / 12',
    inputs: ['s-vol'],
    output: 't-vol',
    working: '3,000,000 / 12',
    answer: '250,000',
    y: 250,
  },
  {
    id: 'MIX-CNP-01',
    playbook: 'domain/channel-mix@1.0.0',
    title: 'Card-not-present is MOTO + e-commerce',
    expression: 'moto + ecomm',
    inputs: ['s-moto', 's-ecom'],
    output: 't-cnp',
    working: '0 + 20',
    answer: '20',
    y: 520,
  },
];

interface RowDef {
  id: string;
  type: string;
  sources: string;
  target: string;
  concept: string;
  transform: string;
  confidence: number;
  status: 'autoAccepted' | 'needsReview';
}
const ROWS: RowDef[] = [
  {
    id: 'M017',
    type: 'oneToOne',
    sources: '$.processing.annualCardVolume',
    target: 'Processing/MonthlyVolume',
    concept: 'ProcessingVolume.CardVolume',
    transform: 'annual / 12',
    confidence: 95,
    status: 'autoAccepted',
  },
  {
    id: 'M018',
    type: 'oneToOne',
    sources: '$.processing.averageTicket',
    target: 'Processing/AverageTicket',
    concept: 'ProcessingVolume.AverageTicket',
    transform: '—',
    confidence: 95,
    status: 'autoAccepted',
  },
  {
    id: 'M019',
    type: 'unmapped',
    sources: '— no source —',
    target: 'Processing/HighTicket',
    concept: 'ProcessingVolume.HighTicket',
    transform: '—',
    confidence: 0,
    status: 'needsReview',
  },
  {
    id: 'M020',
    type: 'oneToOne',
    sources: '$.processing.cardPresentPercent',
    target: 'Processing/CardPresentPct',
    concept: 'ChannelMix.CardPresent',
    transform: '—',
    confidence: 95,
    status: 'autoAccepted',
  },
  {
    id: 'M021',
    type: 'manyToOne',
    sources: '$.processing.motoPercent + ecommPercent',
    target: 'Processing/CardNotPresentPct',
    concept: 'ChannelMix.CardNotPresent',
    transform: 'moto + ecomm',
    confidence: 95,
    status: 'autoAccepted',
  },
];

const XML_LINES = [
  '<Processing>',
  '  <MonthlyVolume>250000</MonthlyVolume>',
  '  <AverageTicket>42.1</AverageTicket>',
  '  <CardPresentPct>80</CardPresentPct>',
  '  <CardNotPresentPct>20</CardNotPresentPct>',
  '</Processing>',
];
const CHECKS = [
  { id: 'VOL-VAL-02', text: 'volume >= 0', result: '250000 ≥ 0' },
  { id: 'MIX-VAL-02', text: 'cp + cnp == 100', result: '80 + 20 = 100' },
  {
    id: 'VOL-VAL-01',
    text: 'avg <= high',
    result: 'skipped · HighTicket unmapped',
  },
];

const CAPTIONS: Record<Scene, string> = {
  systems: 'Two acquiring systems. Two schemas. Nothing in between — no canonical model to translate through.',
  profile: 'MapWright profiles each system from its samples and contracts: paths, types, cardinality — with sample values masked.',
  detect:
    'Playbook vocabulary recognises both volume fields as ProcessingVolume.CardVolume… but one is annual and one is monthly, so they cannot be paired directly.',
  derive: 'A derivation rule bridges the qualifiers: annual / 12. Deterministic, versioned, explainable — the same rule every run.',
  mapping:
    'One row per target field, with evidence, confidence and a review status. HighTicket has no source, so it stays honestly unmapped.',
  ai: 'Where the rules run out, AI may propose — from masked metadata only, capped at 70 %, always reviewed, never replacing a playbook row.',
  replay: 'Replay pushes real sample records through the spec, writes the target document and runs the playbook validations on the result.',
  done: 'Reviewed, replayable, explainable field mappings — and the playbook learns from every approved decision.',
};

const CARD = { w: 380, h: 400, y: 170 };
const SRC_CARD_X = 120;
const TGT_CARD_X = 900;
const CHIP = { w: 320, h: 58 };

@Component({
  selector: 'app-intro',
  imports: [Icon, Logo, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="film" [attr.data-scene]="scene()" data-testid="intro">
      <div class="grid"></div>

      <header class="hud">
        <span class="brand"><app-logo [size]="26" [tile]="true" />MapWright</span>
        <span class="caption" data-testid="intro-caption">{{ caption() }}</span>
        <button mat-stroked-button class="skip" (click)="finish()" data-testid="intro-skip">
          <app-icon name="chevron_right" [size]="18" />Skip intro
        </button>
      </header>

      <svg class="stage" viewBox="0 0 1400 760" preserveAspectRatio="xMidYMid meet" aria-hidden="true">
        <defs>
          <linearGradient id="in-src" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stop-color="#312e81" />
            <stop offset="1" stop-color="#1e1b4b" />
          </linearGradient>
          <linearGradient id="in-tgt" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stop-color="#4c1d95" />
            <stop offset="1" stop-color="#2e1065" />
          </linearGradient>
          <linearGradient id="in-wire" x1="0" y1="0" x2="1" y2="0">
            <stop offset="0" stop-color="#818cf8" />
            <stop offset="1" stop-color="#c084fc" />
          </linearGradient>
          <marker id="in-arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
            <path d="M0 0 L10 5 L0 10 z" fill="#c084fc" />
          </marker>
          <filter id="in-glow"><feGaussianBlur stdDeviation="8" /></filter>
        </defs>

        <!-- Scene 1: the two systems -->
        <g
          class="system src"
          [class.show]="showSystems()"
          [class.ghost]="ghostSystems()"
          [attr.transform]="'translate(' + srcCardX + ' ' + cardY + ')'"
        >
          <rect [attr.width]="cardW" [attr.height]="cardH" rx="26" fill="url(#in-src)" stroke="#6366f1" stroke-width="2" />
          <text x="30" y="46" class="sys-kind">SOURCE · JSON</text>
          <text x="30" y="84" class="sys-name">SalesAlpha</text>
          <text x="30" y="108" class="sys-sub">Sales onboarding application</text>
          <g class="code" transform="translate(30 150)">
            <text y="0">"processing": {{ '{' }}</text>
            <text y="30" x="22">
              "annualCardVolume":
              <tspan class="num">3000000</tspan>
              ,
            </text>
            <text y="60" x="22">
              "averageTicket":
              <tspan class="num">42.1</tspan>
              ,
            </text>
            <text y="90" x="22">
              "cardPresentPercent":
              <tspan class="num">80</tspan>
              ,
            </text>
            <text y="120" x="22">
              "motoPercent":
              <tspan class="num">0</tspan>
              ,
            </text>
            <text y="150" x="22">
              "ecommPercent":
              <tspan class="num">20</tspan>
            </text>
            <text y="180">{{ '}' }}</text>
          </g>
        </g>
        <g
          class="system tgt"
          [class.show]="showSystems()"
          [class.ghost]="ghostSystems()"
          [attr.transform]="'translate(' + tgtCardX + ' ' + cardY + ')'"
        >
          <rect [attr.width]="cardW" [attr.height]="cardH" rx="26" fill="url(#in-tgt)" stroke="#a855f7" stroke-width="2" />
          <text x="30" y="46" class="sys-kind">TARGET · XML</text>
          <text x="30" y="84" class="sys-name">UW Core</text>
          <text x="30" y="108" class="sys-sub">Underwriting request</text>
          <g class="code" transform="translate(30 150)">
            <text y="0">&lt;Processing&gt;</text>
            <text y="30" x="22">&lt;MonthlyVolume /&gt;</text>
            <text y="60" x="22">&lt;AverageTicket /&gt;</text>
            <text y="90" x="22">&lt;HighTicket /&gt;</text>
            <text y="120" x="22">&lt;CardPresentPct /&gt;</text>
            <text y="150" x="22">&lt;CardNotPresentPct /&gt;</text>
            <text y="180">&lt;/Processing&gt;</text>
          </g>
        </g>
        <g class="gap" [class.show]="scene() === 'systems'">
          <path d="M520 370 L 880 370" stroke="#475569" stroke-width="3" stroke-dasharray="10 12" fill="none" />
          <circle cx="700" cy="370" r="44" fill="#0b1027" stroke="#475569" stroke-width="3" />
          <text x="700" y="384" text-anchor="middle" class="gap-q">?</text>
          <text x="700" y="625" text-anchor="middle" class="lead">
            no shared model · no field names in common · different periods and units
          </text>
        </g>

        <!-- Scenes 2–4: field chips -->
        @for (f of fields; track f.id) {
          <g
            class="chip"
            [class.show]="showChips()"
            [class.src]="f.side === 'src'"
            [class.tgt]="f.side === 'tgt'"
            [class.lit]="litChips().has(f.id)"
            [class.faded]="fadeChip(f.id)"
            [style.transition-delay.ms]="f.delay"
            [style.transform]="chipTransform(f)"
          >
            <rect [attr.width]="chipW" [attr.height]="chipH" rx="12" class="chip-bg" />
            <rect x="0" y="0" width="6" [attr.height]="chipH" rx="3" class="chip-bar" />
            <text x="20" y="24" class="chip-path">{{ f.path }}</text>
            <text x="20" y="45" class="chip-meta">
              {{ f.type }} · sample
              <tspan class="mask">{{ f.sample }}</tspan>
            </text>
            <g class="tag" [class.show]="tagged()" [attr.transform]="'translate(' + (chipW - 12) + ' ' + (chipH + 10) + ')'">
              <text text-anchor="end" class="tag-concept">{{ f.concept }}</text>
              @if (f.qualifier) {
                <text text-anchor="end" y="15" class="tag-q" [class.conflict]="conflictQualifier(f.id)">
                  {{ f.qualifier }}
                </text>
              }
            </g>
          </g>
        }

        <!-- Scene 3: the playbook and the qualifier conflict -->
        <g class="book" [class.show]="scene() === 'detect' || scene() === 'derive'" transform="translate(700 690)">
          <rect x="-300" y="-34" width="600" height="64" rx="18" fill="#0f172a" stroke="#4f46e5" stroke-width="1.5" />
          <text x="-278" y="-8" class="book-kind">DOMAIN PLAYBOOKS</text>
          <text x="-278" y="14" class="book-name">domain/processing-volume&#64;1.0.0 · domain/channel-mix&#64;1.0.0</text>
          <text x="280" y="4" text-anchor="end" class="book-state">published</text>
        </g>
        <g class="conflict" [class.show]="scene() === 'detect'">
          <path
            [attr.d]="wire(chipRight('s-vol'), chipLeft('t-vol'))"
            stroke="#f87171"
            stroke-width="3"
            stroke-dasharray="6 10"
            fill="none"
          />
          <g transform="translate(700 150)">
            <rect x="-200" y="-28" width="400" height="56" rx="14" fill="#2a0f16" stroke="#f87171" stroke-width="1.5" />
            <text y="-4" text-anchor="middle" class="conflict-t">same concept, period conflict</text>
            <text y="16" text-anchor="middle" class="conflict-s">annual ≠ monthly · ByConcept refuses to pair</text>
          </g>
        </g>

        <!-- Scene 4: derivation rules -->
        @for (r of rules; track r.id) {
          <g class="rule" [class.show]="scene() === 'derive'" [class.solved]="solved().has(r.id)">
            @for (i of r.inputs; track i) {
              <path class="wire in" [attr.d]="wire(chipRight(i), { x: 560, y: r.y })" />
            }
            <path class="wire out" [attr.d]="wire({ x: 840, y: r.y }, chipLeft(r.output))" marker-end="url(#in-arrow)" />
            <g [attr.transform]="'translate(700 ' + r.y + ')'">
              <rect x="-140" y="-78" width="280" height="156" rx="18" class="rule-bg" />
              <text x="-122" y="-50" class="rule-id">{{ r.id }}</text>
              <text x="122" y="-50" text-anchor="end" class="rule-kind">derivation</text>
              <text x="-122" y="-28" class="rule-title">{{ r.title }}</text>
              <rect x="-122" y="-12" width="244" height="34" rx="8" class="expr-bg" />
              <text y="11" text-anchor="middle" class="expr">
                {{ r.expression }}
              </text>
              <text y="46" text-anchor="middle" class="working">
                {{ solved().has(r.id) ? r.working + ' = ' : r.working }}
                <tspan class="answer">
                  {{ solved().has(r.id) ? r.answer : '' }}
                </tspan>
              </text>
              <text y="66" text-anchor="middle" class="rule-pb">
                {{ r.playbook }}
              </text>
            </g>
          </g>
        }

        <!-- Scene 8: the mark assembles -->
        <g class="finale" [class.show]="scene() === 'done'" transform="translate(700 300)">
          <circle r="150" fill="#6366f1" opacity=".22" filter="url(#in-glow)" />
        </g>
      </svg>

      <!-- Scene 5–6: the mapping spec -->
      <div
        class="sheet mapping"
        [class.show]="scene() === 'mapping' || scene() === 'ai'"
        [class.dim]="scene() === 'ai'"
        data-testid="intro-mapping"
      >
        <div class="sheet-head">
          <div>
            <div class="sheet-kind">MAPPING SPEC · sales-alpha__uw-core</div>
            <div class="sheet-title">SalesAlpha → UW Core <span class="v">v0.1.0</span></div>
          </div>
          <div class="sheet-stats">
            <span><b>5</b> target fields</span><span><b>4</b> mapped</span><span><b>4</b> auto-accepted</span
            ><span><b>1</b> needs review</span>
          </div>
        </div>
        <table>
          <thead>
            <tr>
              <th>Row</th>
              <th>Type</th>
              <th>Source</th>
              <th>Target</th>
              <th>Concept</th>
              <th>Transformation</th>
              <th>Confidence</th>
              <th>Review</th>
            </tr>
          </thead>
          <tbody>
            @for (r of rows; track r.id; let i = $index) {
              <tr [class.show]="shownRows() > i" [class.focus]="scene() === 'ai' && r.id === 'M019'" [attr.data-row]="r.id">
                <td class="mono">{{ r.id }}</td>
                <td>
                  <span class="type" [attr.data-type]="r.type">{{ r.type }}</span>
                </td>
                <td class="mono">{{ r.sources }}</td>
                <td class="mono">{{ r.target }}</td>
                <td class="concept">{{ r.concept }}</td>
                <td class="mono expr-cell">{{ r.transform }}</td>
                <td>
                  <span class="bar"><i [style.width.%]="shownRows() > i ? r.confidence : 0"></i></span>{{ r.confidence }}%
                </td>
                <td>
                  <span class="status" [attr.data-status]="r.status">{{
                    r.status === 'autoAccepted' ? 'auto-accepted' : 'needs review'
                  }}</span>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>

      <div class="ai-card" [class.show]="scene() === 'ai'" data-testid="intro-ai">
        <div class="ai-head"><app-icon name="star_shine" [size]="20" />AI assist · row M019 · Processing/HighTicket</div>
        <div class="ai-body">
          <div class="ai-sent">
            <span class="k">sent</span> field names, types, cardinality, concept <span class="k">withheld</span> sample values, ranges, free
            text
          </div>
          <div class="ai-sugg">
            <div class="ai-q">No deterministic rule produces HighTicket from SalesAlpha.</div>
            <div class="ai-a">
              Suggestion: <code>$.processing.averageTicket</code> as a lower bound?
              <span class="ai-conf">confidence 64 % · capped at 70 %</span>
            </div>
          </div>
          <div class="ai-foot">
            <span class="pill review">needs review</span><span class="pill">never auto-accepted</span
            ><span class="pill">approve → playbook draft</span>
          </div>
        </div>
      </div>

      <!-- Scene 7: replay -->
      <div class="replay" [class.show]="scene() === 'replay'" data-testid="intro-replay">
        <div class="pane in">
          <div class="pane-kind">SAMPLE · corp-three-owners.json</div>
          <pre
            >{{ '{' }}
  "processing": {{ '{' }}
    "annualCardVolume": <b>3000000</b>,
    "averageTicket": <b>42.1</b>,
    "cardPresentPercent": <b>80</b>,
    "motoPercent": <b>0</b>,
    "ecommPercent": <b>20</b>
  {{ '}' }}
{{ '}' }}</pre>
        </div>
        <div class="engine">
          <div class="engine-box">
            <div class="engine-t">mapwright replay</div>
            <div class="engine-s">spec v0.1.0 · 5 rows</div>
            <div class="pulse"></div>
          </div>
          <div class="checks">
            @for (c of checks; track c.id; let i = $index) {
              <div class="check" [class.show]="shownChecks() > i" [class.skip]="c.result.startsWith('skipped')">
                <app-icon [name]="c.result.startsWith('skipped') ? 'schedule' : 'check_circle'" [size]="16" />
                <span class="mono">{{ c.id }}</span
                ><span class="mono dim">{{ c.text }}</span
                ><span class="res">{{ c.result }}</span>
              </div>
            }
          </div>
        </div>
        <div class="pane out">
          <div class="pane-kind">OUTPUT · corp-three-owners.xml</div>
          <pre>@for (l of xml; track $index; let i = $index) {<span class="line" [class.show]="shownXml() > i" [class.hot]="i === 1">{{ l }}
</span>}</pre>
        </div>
      </div>

      <!-- Scene 8 -->
      @if (scene() === 'done') {
        <div class="finale-html" data-testid="intro-done">
          <app-logo [size]="120" [tile]="true" class="big" />
          <h1>Map<b>Wright</b></h1>
          <p>Playbook-driven field mapping between acquiring systems</p>
          <div class="chips">
            <span class="chip-ok">deterministic first</span>
            <span class="chip-ok">every row explained</span>
            <span class="chip-ok">replayable on real samples</span>
            <span class="chip-ok">AI only where rules run out</span>
          </div>
          <button mat-flat-button class="enter" (click)="finish()" data-testid="intro-enter">
            Open MapWright<app-icon name="arrow_forward" [size]="20" />
          </button>
        </div>
      }

      <footer class="progress">
        @for (s of order; track s) {
          <button
            type="button"
            class="dot"
            [class.on]="s === scene()"
            [class.past]="isPast(s)"
            (click)="jump(s)"
            [attr.aria-label]="'Scene ' + s"
          ></button>
        }
      </footer>
    </div>
  `,
  styles: [
    `
      :host {
        position: fixed;
        inset: 0;
        z-index: 1000;
        display: block;
      }
      .film {
        position: absolute;
        inset: 0;
        overflow: hidden;
        background: radial-gradient(ellipse at 50% 0%, #1e1b4b 0%, #0b1027 55%, #070a1a 100%);
        color: #e2e8f0;
        font-family:
          system-ui,
          -apple-system,
          'Segoe UI',
          Roboto,
          sans-serif;
        animation: fade 0.6s ease both;
      }
      .grid {
        position: absolute;
        inset: 0;
        background-image:
          linear-gradient(rgba(129, 140, 248, 0.07) 1px, transparent 1px),
          linear-gradient(90deg, rgba(129, 140, 248, 0.07) 1px, transparent 1px);
        background-size: 48px 48px;
        mask-image: radial-gradient(ellipse at center, #000 30%, transparent 85%);
      }

      /* HUD */
      .hud {
        position: absolute;
        top: 0;
        left: 0;
        right: 0;
        z-index: 3;
        display: flex;
        align-items: center;
        gap: 22px;
        padding: 16px 26px;
      }
      .brand {
        display: inline-flex;
        align-items: center;
        gap: 10px;
        font-weight: 800;
        font-size: 17px;
        letter-spacing: -0.01em;
        color: #fff;
        white-space: nowrap;
      }
      .brand app-logo {
        border-radius: 7px;
      }
      .caption {
        flex: 1;
        text-align: center;
        font-size: 16px;
        color: #c7d2fe;
        min-height: 1.4em;
        animation: caption 0.7s ease both;
      }
      @keyframes caption {
        from {
          opacity: 0;
          transform: translateY(6px);
        }
        to {
          opacity: 1;
          transform: none;
        }
      }
      .skip {
        --mdc-outlined-button-label-text-color: #c7d2fe;
        --mdc-outlined-button-outline-color: rgba(199, 210, 254, 0.35);
        white-space: nowrap;
      }
      .skip app-icon {
        margin-right: 4px;
      }

      /* stage */
      .stage {
        position: absolute;
        left: 0;
        right: 0;
        top: 56px;
        bottom: 34px;
        width: 100%;
        height: calc(100% - 90px);
      }
      .lead {
        fill: #94a3b8;
        font-size: 15px;
        font-style: italic;
      }

      .system {
        opacity: 0;
        transition:
          opacity 0.9s ease,
          filter 1s ease;
      }
      .system.show {
        opacity: 1;
      }
      .system.ghost {
        opacity: 0.06;
        filter: blur(2px);
      }
      .system.src {
        animation: from-left 1s cubic-bezier(0.2, 0.8, 0.2, 1) both;
      }
      .system.tgt {
        animation: from-right 1s cubic-bezier(0.2, 0.8, 0.2, 1) both;
      }
      @keyframes from-left {
        from {
          translate: -140px 0;
        }
      }
      @keyframes from-right {
        from {
          translate: 140px 0;
        }
      }
      .sys-kind {
        fill: #a5b4fc;
        font-size: 11px;
        font-weight: 700;
        letter-spacing: 0.14em;
      }
      .tgt .sys-kind {
        fill: #d8b4fe;
      }
      .sys-name {
        fill: #fff;
        font-size: 30px;
        font-weight: 800;
        letter-spacing: -0.02em;
      }
      .sys-sub {
        fill: #94a3b8;
        font-size: 13px;
      }
      .code text {
        fill: #cbd5e1;
        font-family: ui-monospace, 'Cascadia Code', Menlo, Consolas, monospace;
        font-size: 15px;
      }
      .code .num {
        fill: #86efac;
      }
      .gap {
        opacity: 0;
        transition: opacity 0.6s ease 0.8s;
      }
      .gap.show {
        opacity: 1;
      }
      .gap-q {
        fill: #94a3b8;
        font-size: 44px;
        font-weight: 800;
      }

      /* chips */
      .chip {
        opacity: 0;
        transition:
          transform 1.3s cubic-bezier(0.2, 0.8, 0.2, 1),
          opacity 0.6s ease;
      }
      .chip.show {
        opacity: 1;
      }
      .chip.faded {
        opacity: 0.22;
      }
      .chip-bg {
        fill: #0f172a;
        stroke: #334155;
        stroke-width: 1.5;
        transition:
          stroke 0.5s,
          fill 0.5s;
      }
      .chip.src .chip-bar {
        fill: #6366f1;
      }
      .chip.tgt .chip-bar {
        fill: #a855f7;
      }
      .chip.lit .chip-bg {
        fill: #14213d;
        stroke: #818cf8;
      }
      .chip-path {
        fill: #f1f5f9;
        font-family: ui-monospace, 'Cascadia Code', Menlo, Consolas, monospace;
        font-size: 14px;
        font-weight: 600;
      }
      .chip-meta {
        fill: #94a3b8;
        font-size: 12px;
      }
      .mask {
        fill: #fbbf24;
        font-family: ui-monospace, monospace;
        letter-spacing: 0.08em;
      }
      .tag {
        opacity: 0;
        transition: opacity 0.6s ease;
      }
      .tag.show {
        opacity: 1;
      }
      .tag-concept {
        fill: #a5b4fc;
        font-size: 11.5px;
        font-weight: 700;
      }
      .tag-q {
        fill: #64748b;
        font-size: 11px;
        font-family: ui-monospace, monospace;
      }
      .tag-q.conflict {
        fill: #f87171;
        font-weight: 700;
      }

      /* playbook + conflict */
      .book,
      .conflict {
        opacity: 0;
        transition: opacity 0.7s ease 0.4s;
      }
      .book.show,
      .conflict.show {
        opacity: 1;
      }
      .book-kind {
        fill: #818cf8;
        font-size: 10px;
        font-weight: 700;
        letter-spacing: 0.14em;
      }
      .book-name {
        fill: #e2e8f0;
        font-size: 13px;
        font-family: ui-monospace, monospace;
      }
      .book-state {
        fill: #86efac;
        font-size: 12px;
        font-weight: 700;
      }
      .conflict path {
        animation: dash 1s linear infinite;
      }
      .conflict-t {
        fill: #fecaca;
        font-size: 14px;
        font-weight: 700;
      }
      .conflict-s {
        fill: #f87171;
        font-size: 12px;
        font-family: ui-monospace, monospace;
      }

      /* rules */
      .rule {
        opacity: 0;
        transition: opacity 0.7s ease 0.3s;
      }
      .rule.show {
        opacity: 1;
      }
      .wire {
        fill: none;
        stroke: url(#in-wire);
        stroke-width: 3;
        stroke-dasharray: 10 10;
        animation: dash 0.8s linear infinite;
      }
      .wire.out {
        opacity: 0;
        transition: opacity 0.5s;
      }
      .rule.solved .wire.out {
        opacity: 1;
      }
      @keyframes dash {
        to {
          stroke-dashoffset: -40;
        }
      }
      .rule-bg {
        fill: #0f172a;
        stroke: #6366f1;
        stroke-width: 2;
        transition: stroke 0.5s;
      }
      .rule.solved .rule-bg {
        stroke: #34d399;
      }
      .rule-id {
        fill: #a5b4fc;
        font-size: 12px;
        font-weight: 800;
        font-family: ui-monospace, monospace;
      }
      .rule-kind {
        fill: #64748b;
        font-size: 11px;
        letter-spacing: 0.1em;
      }
      .rule-title {
        fill: #e2e8f0;
        font-size: 13px;
      }
      .expr-bg {
        fill: #1e1b4b;
        stroke: #4f46e5;
      }
      .expr {
        fill: #fff;
        font-size: 19px;
        font-weight: 700;
        font-family: ui-monospace, monospace;
      }
      .working {
        fill: #94a3b8;
        font-size: 14px;
        font-family: ui-monospace, monospace;
      }
      .answer {
        fill: #34d399;
        font-weight: 800;
        font-size: 17px;
      }
      .rule-pb {
        fill: #64748b;
        font-size: 10.5px;
        font-family: ui-monospace, monospace;
      }

      /* sheets */
      .sheet {
        position: absolute;
        left: 50%;
        top: 50%;
        width: min(1180px, 94vw);
        translate: -50% -50%;
        opacity: 0;
        transform: translateY(30px) scale(0.97);
        transition:
          opacity 0.7s ease,
          transform 0.9s cubic-bezier(0.2, 0.8, 0.2, 1);
        pointer-events: none;
        background: #fff;
        color: #141a33;
        border-radius: 16px;
        box-shadow: 0 40px 120px rgba(0, 0, 0, 0.6);
        overflow: hidden;
      }
      .sheet.show {
        opacity: 1;
        transform: none;
      }
      .sheet.dim {
        opacity: 0.35;
        filter: blur(1px);
        transform: scale(0.96);
      }
      .sheet-head {
        display: flex;
        justify-content: space-between;
        align-items: flex-end;
        padding: 18px 24px 12px;
        border-bottom: 1px solid #e6e8f2;
      }
      .sheet-kind {
        font-size: 10.5px;
        font-weight: 700;
        letter-spacing: 0.12em;
        color: #6b7394;
      }
      .sheet-title {
        font-size: 20px;
        font-weight: 800;
        margin-top: 2px;
      }
      .sheet-title .v {
        font-size: 12px;
        color: #6b7394;
        margin-left: 8px;
        font-weight: 600;
      }
      .sheet-stats {
        display: flex;
        gap: 18px;
        font-size: 12.5px;
        color: #6b7394;
      }
      .sheet-stats b {
        color: #141a33;
        font-size: 16px;
        margin-right: 4px;
      }
      table {
        width: 100%;
        border-collapse: collapse;
        table-layout: fixed;
        font-size: 12px;
      }
      th:nth-child(1) {
        width: 6%;
      }
      th:nth-child(2) {
        width: 10%;
      }
      th:nth-child(3) {
        width: 19%;
      }
      th:nth-child(4) {
        width: 16%;
      }
      th:nth-child(5) {
        width: 17%;
      }
      th:nth-child(6) {
        width: 9%;
      }
      th:nth-child(7) {
        width: 11%;
      }
      th:nth-child(8) {
        width: 12%;
      }
      td.mono {
        overflow-wrap: anywhere;
      }
      .type,
      .status {
        white-space: nowrap;
      }
      th {
        text-align: left;
        font-size: 10.5px;
        letter-spacing: 0.08em;
        text-transform: uppercase;
        color: #6b7394;
        padding: 10px 10px;
        background: #f5f6fb;
      }
      td {
        padding: 12px 10px;
        border-top: 1px solid #eef0f6;
        vertical-align: middle;
      }
      tbody tr {
        opacity: 0;
        transform: translateX(-16px);
        transition:
          opacity 0.5s ease,
          transform 0.6s cubic-bezier(0.2, 0.8, 0.2, 1),
          background 0.4s;
      }
      tbody tr.show {
        opacity: 1;
        transform: none;
      }
      tbody tr.focus {
        background: #f1ebff;
        outline: 2px solid #8b5cf6;
      }
      .mono {
        font-family: ui-monospace, 'Cascadia Code', Menlo, Consolas, monospace;
        font-size: 12px;
      }
      .concept {
        color: #4f46e5;
        font-weight: 600;
      }
      .expr-cell {
        color: #7c3aed;
        font-weight: 700;
      }
      .type {
        padding: 2px 8px;
        border-radius: 999px;
        font-size: 11px;
        font-weight: 600;
        background: #e9ecff;
        color: #4f46e5;
      }
      .type[data-type='manyToOne'] {
        background: #e6f6fd;
        color: #0369a1;
      }
      .type[data-type='unmapped'] {
        background: #fdecec;
        color: #b91c1c;
      }
      .bar {
        display: inline-block;
        width: 70px;
        height: 7px;
        border-radius: 99px;
        background: #eef0f6;
        margin-right: 8px;
        vertical-align: middle;
        overflow: hidden;
      }
      .bar i {
        display: block;
        height: 100%;
        background: linear-gradient(90deg, #6366f1, #a855f7);
        transition: width 1.4s cubic-bezier(0.2, 0.8, 0.2, 1) 0.3s;
      }
      .status {
        padding: 3px 9px;
        border-radius: 999px;
        font-size: 11px;
        font-weight: 700;
      }
      .status[data-status='autoAccepted'] {
        background: #e7f8f1;
        color: #047857;
      }
      .status[data-status='needsReview'] {
        background: #fff4df;
        color: #b45309;
      }

      /* AI card */
      .ai-card {
        position: absolute;
        left: 50%;
        top: 50%;
        width: min(720px, 90vw);
        translate: -50% -50%;
        transform: translateY(40px);
        opacity: 0;
        transition:
          opacity 0.6s ease 0.5s,
          transform 0.8s cubic-bezier(0.2, 0.8, 0.2, 1) 0.5s;
        background: #1a1330;
        border: 1.5px solid #8b5cf6;
        border-radius: 18px;
        box-shadow: 0 30px 90px rgba(139, 92, 246, 0.35);
        pointer-events: none;
      }
      .ai-card.show {
        opacity: 1;
        transform: none;
      }
      .ai-head {
        display: flex;
        align-items: center;
        gap: 10px;
        padding: 14px 20px;
        font-weight: 700;
        color: #d8b4fe;
        border-bottom: 1px solid rgba(139, 92, 246, 0.3);
      }
      .ai-body {
        padding: 16px 20px 18px;
        display: grid;
        gap: 14px;
        font-size: 13.5px;
      }
      .ai-sent {
        color: #a5b4fc;
        font-size: 12.5px;
      }
      .ai-sent .k {
        display: inline-block;
        font-size: 10px;
        font-weight: 800;
        letter-spacing: 0.1em;
        text-transform: uppercase;
        color: #fbbf24;
        margin: 0 6px 0 0;
      }
      .ai-sent .k + .k,
      .ai-sent .k:nth-of-type(2) {
        margin-left: 14px;
      }
      .ai-sugg {
        background: rgba(255, 255, 255, 0.04);
        border-radius: 12px;
        padding: 12px 14px;
        display: grid;
        gap: 6px;
      }
      .ai-q {
        color: #94a3b8;
      }
      .ai-a {
        color: #f1f5f9;
      }
      .ai-a code {
        font-family: ui-monospace, monospace;
        color: #c4b5fd;
      }
      .ai-conf {
        margin-left: 10px;
        color: #fbbf24;
        font-weight: 700;
        font-size: 12px;
      }
      .ai-foot {
        display: flex;
        gap: 8px;
        flex-wrap: wrap;
      }
      .pill {
        padding: 4px 10px;
        border-radius: 999px;
        font-size: 11.5px;
        border: 1px solid rgba(255, 255, 255, 0.15);
        color: #cbd5e1;
      }
      .pill.review {
        border-color: #f59e0b;
        color: #fcd34d;
        font-weight: 700;
      }

      /* replay */
      .replay {
        position: absolute;
        left: 50%;
        top: 50%;
        width: min(1240px, 96vw);
        translate: -50% -50%;
        display: grid;
        grid-template-columns: 1fr 1.1fr 1fr;
        gap: 22px;
        align-items: center;
        opacity: 0;
        transform: translateY(30px);
        transition:
          opacity 0.7s ease,
          transform 0.9s cubic-bezier(0.2, 0.8, 0.2, 1);
        pointer-events: none;
      }
      .replay.show {
        opacity: 1;
        transform: none;
      }
      .pane {
        background: #0f172a;
        border: 1.5px solid #334155;
        border-radius: 16px;
        padding: 16px 18px 6px;
        min-height: 250px;
      }
      .pane.in {
        border-color: #6366f1;
      }
      .pane.out {
        border-color: #a855f7;
      }
      .pane-kind {
        font-size: 10.5px;
        font-weight: 700;
        letter-spacing: 0.12em;
        color: #94a3b8;
        margin-bottom: 8px;
      }
      pre {
        margin: 0;
        font-family: ui-monospace, 'Cascadia Code', Menlo, Consolas, monospace;
        font-size: 13.5px;
        line-height: 1.6;
        color: #cbd5e1;
        white-space: pre;
      }
      pre b {
        color: #86efac;
        font-weight: 600;
      }
      .line {
        display: block;
        opacity: 0;
        transform: translateX(10px);
        transition:
          opacity 0.35s,
          transform 0.4s;
      }
      .line.show {
        opacity: 1;
        transform: none;
      }
      .line.hot {
        color: #fff;
        background: rgba(52, 211, 153, 0.14);
        border-radius: 4px;
      }
      .engine {
        display: grid;
        gap: 14px;
        justify-items: center;
      }
      .engine-box {
        position: relative;
        text-align: center;
        padding: 16px 28px;
        border-radius: 14px;
        background: linear-gradient(135deg, #312e81, #4c1d95);
        border: 1.5px solid #818cf8;
        box-shadow: 0 0 60px rgba(99, 102, 241, 0.4);
      }
      .engine-t {
        font-family: ui-monospace, monospace;
        font-weight: 800;
        color: #fff;
        font-size: 16px;
      }
      .engine-s {
        color: #c7d2fe;
        font-size: 12px;
        margin-top: 2px;
      }
      .pulse {
        position: absolute;
        inset: -6px;
        border-radius: 18px;
        border: 2px solid #a5b4fc;
        animation: pulse 1.6s ease-out infinite;
        pointer-events: none;
      }
      @keyframes pulse {
        from {
          opacity: 0.8;
          transform: scale(1);
        }
        to {
          opacity: 0;
          transform: scale(1.15);
        }
      }
      .checks {
        display: grid;
        gap: 6px;
        width: 100%;
      }
      .check {
        display: flex;
        align-items: center;
        gap: 10px;
        font-size: 12.5px;
        padding: 8px 12px;
        border-radius: 10px;
        background: rgba(16, 185, 129, 0.1);
        border: 1px solid rgba(16, 185, 129, 0.35);
        color: #a7f3d0;
        opacity: 0;
        transform: translateY(8px);
        transition:
          opacity 0.4s,
          transform 0.5s;
      }
      .check.show {
        opacity: 1;
        transform: none;
      }
      .check.skip {
        background: rgba(148, 163, 184, 0.08);
        border-color: rgba(148, 163, 184, 0.3);
        color: #cbd5e1;
      }
      .check .dim {
        color: #94a3b8;
      }
      .check .res {
        margin-left: auto;
        font-weight: 700;
      }

      /* finale */
      .finale {
        opacity: 0;
        transition: opacity 1.2s ease;
      }
      .finale.show {
        opacity: 1;
      }
      .finale-html {
        position: absolute;
        inset: 56px 0 34px;
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        gap: 14px;
        animation: rise 0.9s cubic-bezier(0.2, 0.8, 0.2, 1) both;
      }
      @keyframes rise {
        from {
          opacity: 0;
          transform: translateY(30px) scale(0.94);
        }
        to {
          opacity: 1;
          transform: none;
        }
      }
      .finale-html .big {
        border-radius: 30px;
        box-shadow: 0 30px 80px rgba(124, 58, 237, 0.55);
        animation: spin-in 1.1s cubic-bezier(0.2, 0.8, 0.2, 1) both;
      }
      @keyframes spin-in {
        from {
          transform: rotate(-90deg) scale(0.4);
          opacity: 0;
        }
      }
      .finale-html h1 {
        margin: 10px 0 0;
        font-size: 56px;
        line-height: 1.1;
        font-weight: 800;
        letter-spacing: -0.03em;
        color: #fff;
      }
      .finale-html h1 b {
        background: linear-gradient(90deg, #a5b4fc, #d8b4fe);
        -webkit-background-clip: text;
        background-clip: text;
        color: transparent;
      }
      .finale-html p {
        margin: 10px 0 0;
        color: #a5b4fc;
        font-size: 17px;
      }
      .chips {
        display: flex;
        gap: 10px;
        flex-wrap: wrap;
        justify-content: center;
        margin-top: 16px;
      }
      .chip-ok {
        padding: 6px 14px;
        border-radius: 999px;
        border: 1px solid rgba(52, 211, 153, 0.5);
        color: #6ee7b7;
        font-size: 12.5px;
        background: rgba(52, 211, 153, 0.06);
      }
      .enter {
        margin-top: 18px;
        height: 48px !important;
        padding: 0 26px !important;
        font-size: 16px !important;
        --mdc-filled-button-container-color: #6366f1;
        --mdc-filled-button-label-text-color: #fff;
      }
      .enter app-icon {
        margin-left: 8px;
      }

      /* progress */
      .progress {
        position: absolute;
        left: 0;
        right: 0;
        bottom: 12px;
        display: flex;
        justify-content: center;
        gap: 8px;
        z-index: 3;
      }
      .dot {
        width: 26px;
        height: 5px;
        border-radius: 99px;
        border: 0;
        padding: 0;
        background: rgba(199, 210, 254, 0.18);
        cursor: pointer;
        transition:
          background 0.4s,
          width 0.4s;
      }
      .dot.past {
        background: rgba(199, 210, 254, 0.45);
      }
      .dot.on {
        width: 44px;
        background: linear-gradient(90deg, #818cf8, #c084fc);
      }

      @keyframes fade {
        from {
          opacity: 0;
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .film * {
          transition-duration: 0.01s !important;
          animation-duration: 0.01s !important;
        }
      }
    `,
  ],
})
export class Intro {
  private readonly destroyRef = inject(DestroyRef);
  readonly done = output<void>();

  protected readonly order = ORDER;
  protected readonly fields = FIELDS;
  protected readonly rules = RULES;
  protected readonly rows = ROWS;
  protected readonly xml = XML_LINES;
  protected readonly checks = CHECKS;
  protected readonly cardW = CARD.w;
  protected readonly cardH = CARD.h;
  protected readonly cardY = CARD.y;
  protected readonly srcCardX = SRC_CARD_X;
  protected readonly tgtCardX = TGT_CARD_X;
  protected readonly chipW = CHIP.w;
  protected readonly chipH = CHIP.h;

  readonly scene = signal<Scene>('systems');
  protected readonly litChips = signal<Set<string>>(new Set());
  protected readonly solved = signal<Set<string>>(new Set());
  protected readonly shownRows = signal(0);
  protected readonly shownXml = signal(0);
  protected readonly shownChecks = signal(0);

  protected readonly caption = computed(() => CAPTIONS[this.scene()]);
  protected readonly showSystems = computed(() => ['systems', 'profile', 'detect', 'derive'].includes(this.scene()));
  protected readonly ghostSystems = computed(() => ['profile', 'detect', 'derive'].includes(this.scene()));
  protected readonly showChips = computed(() => ['profile', 'detect', 'derive'].includes(this.scene()));
  protected readonly tagged = computed(() => ['detect', 'derive'].includes(this.scene()));

  private readonly timers: ReturnType<typeof setTimeout>[] = [];
  private readonly chipHome = new Map<string, Pt>();

  constructor() {
    let s = 0,
      t = 0;
    for (const f of FIELDS) {
      const i = f.side === 'src' ? s++ : t++;
      const x = f.side === 'src' ? 180 : 900;
      this.chipHome.set(f.id, { x, y: 130 + i * 112 });
    }
    this.enter('systems');
    this.destroyRef.onDestroy(() => this.timers.forEach(clearTimeout));
  }

  /** Where a chip sits: folded inside its system card until the profile scene lets it out. */
  protected chipTransform(f: FieldDef): string {
    if (!this.showChips()) {
      const cx = f.side === 'src' ? SRC_CARD_X + CARD.w / 2 - CHIP.w / 2 : TGT_CARD_X + CARD.w / 2 - CHIP.w / 2;
      return `translate(${cx}px, ${CARD.y + CARD.h / 2 - CHIP.h / 2}px) scale(0.6)`;
    }
    const p = this.chipHome.get(f.id)!;
    return `translate(${p.x}px, ${p.y}px)`;
  }

  protected chipRight(id: string): Pt {
    const p = this.chipHome.get(id)!;
    return { x: p.x + CHIP.w, y: p.y + CHIP.h / 2 };
  }

  protected chipLeft(id: string): Pt {
    const p = this.chipHome.get(id)!;
    return { x: p.x, y: p.y + CHIP.h / 2 };
  }

  protected wire(a: Pt, b: Pt): string {
    const mx = (a.x + b.x) / 2;
    return `M${a.x} ${a.y} C ${mx} ${a.y}, ${mx} ${b.y}, ${b.x} ${b.y}`;
  }

  protected conflictQualifier(id: string): boolean {
    return this.scene() === 'detect' && (id === 's-vol' || id === 't-vol');
  }

  /** In the derive scene only the fields a rule touches stay bright. */
  protected fadeChip(id: string): boolean {
    if (this.scene() !== 'derive') return false;
    return !RULES.some((r) => r.inputs.includes(id) || r.output === id);
  }

  protected isPast(s: Scene): boolean {
    return ORDER.indexOf(s) < ORDER.indexOf(this.scene());
  }

  protected jump(s: Scene): void {
    this.timers.forEach(clearTimeout);
    this.timers.length = 0;
    this.enter(s);
  }

  private later(fn: () => void, ms: number): void {
    this.timers.push(setTimeout(fn, ms));
  }

  private enter(scene: Scene): void {
    this.scene.set(scene);
    this.litChips.set(new Set());
    this.solved.set(new Set());
    this.shownRows.set(0);
    this.shownXml.set(0);
    this.shownChecks.set(0);

    if (scene === 'detect') {
      FIELDS.forEach((f, i) => this.later(() => this.litChips.update((s) => new Set([...s, f.id])), 500 + i * 160));
    }
    if (scene === 'derive') {
      RULES.forEach((r, i) => this.later(() => this.solved.update((s) => new Set([...s, r.id])), 2400 + i * 1400));
    }
    if (scene === 'mapping') {
      ROWS.forEach((_, i) => this.later(() => this.shownRows.set(i + 1), 900 + i * 650));
    }
    if (scene === 'ai') {
      this.shownRows.set(ROWS.length);
    }
    if (scene === 'replay') {
      XML_LINES.forEach((_, i) => this.later(() => this.shownXml.set(i + 1), 1400 + i * 420));
      CHECKS.forEach((_, i) => this.later(() => this.shownChecks.set(i + 1), 4200 + i * 700));
    }

    const idx = ORDER.indexOf(scene);
    const ms = SCENES[idx].ms;
    if (ms > 0) this.later(() => this.enter(ORDER[idx + 1]), ms);
  }

  finish(): void {
    this.timers.forEach(clearTimeout);
    this.done.emit();
  }
}
