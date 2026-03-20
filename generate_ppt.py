"""
중간보고 PPT 생성: 강화학습 기반 다중 무인 수상정 협력 제어
"""
from pptx import Presentation
from pptx.util import Inches, Pt, Emu
from pptx.dml.color import RGBColor
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR
from pptx.enum.shapes import MSO_SHAPE
import os

prs = Presentation()
prs.slide_width = Inches(13.333)
prs.slide_height = Inches(7.5)

# ── 색상 팔레트 ──
NAVY = RGBColor(0x0B, 0x1D, 0x3A)
DARK_BLUE = RGBColor(0x14, 0x2D, 0x5E)
ACCENT_BLUE = RGBColor(0x1E, 0x90, 0xFF)
LIGHT_BLUE = RGBColor(0x87, 0xCE, 0xEB)
WHITE = RGBColor(0xFF, 0xFF, 0xFF)
LIGHT_GRAY = RGBColor(0xF0, 0xF0, 0xF0)
DARK_GRAY = RGBColor(0x33, 0x33, 0x33)
ORANGE = RGBColor(0xFF, 0x8C, 0x00)
GREEN = RGBColor(0x2E, 0xCC, 0x71)
RED = RGBColor(0xE7, 0x4C, 0x3C)


def add_bg(slide, color=NAVY):
    bg = slide.background
    fill = bg.fill
    fill.solid()
    fill.fore_color.rgb = color


def add_textbox(slide, left, top, width, height, text, font_size=18,
                color=WHITE, bold=False, alignment=PP_ALIGN.LEFT, font_name="맑은 고딕"):
    txBox = slide.shapes.add_textbox(Inches(left), Inches(top), Inches(width), Inches(height))
    tf = txBox.text_frame
    tf.word_wrap = True
    p = tf.paragraphs[0]
    p.text = text
    p.font.size = Pt(font_size)
    p.font.color.rgb = color
    p.font.bold = bold
    p.font.name = font_name
    p.alignment = alignment
    return tf


def add_para(tf, text, font_size=18, color=WHITE, bold=False, alignment=PP_ALIGN.LEFT,
             space_before=Pt(6), font_name="맑은 고딕"):
    p = tf.add_paragraph()
    p.text = text
    p.font.size = Pt(font_size)
    p.font.color.rgb = color
    p.font.bold = bold
    p.font.name = font_name
    p.alignment = alignment
    if space_before:
        p.space_before = space_before
    return p


def add_shape_box(slide, left, top, width, height, fill_color=DARK_BLUE, border_color=ACCENT_BLUE):
    shape = slide.shapes.add_shape(MSO_SHAPE.ROUNDED_RECTANGLE,
                                    Inches(left), Inches(top), Inches(width), Inches(height))
    shape.fill.solid()
    shape.fill.fore_color.rgb = fill_color
    shape.line.color.rgb = border_color
    shape.line.width = Pt(1.5)
    return shape


def add_accent_line(slide, top=1.35):
    shape = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE,
                                    Inches(0.8), Inches(top), Inches(2), Inches(0.05))
    shape.fill.solid()
    shape.fill.fore_color.rgb = ACCENT_BLUE
    shape.line.fill.background()


# ════════════════════════════════════════
# Slide 1: 표지
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)

# 상단 라인
shape = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE,
                                Inches(0), Inches(0), Inches(13.333), Inches(0.08))
shape.fill.solid()
shape.fill.fore_color.rgb = ACCENT_BLUE
shape.line.fill.background()

add_textbox(slide, 1.5, 1.5, 10, 0.5, "중간 보고", 20, LIGHT_BLUE, False, PP_ALIGN.CENTER)
add_textbox(slide, 1.5, 2.2, 10, 1.5, "강화학습 기반\n다중 무인 수상정 협력 제어",
            44, WHITE, True, PP_ALIGN.CENTER)
add_textbox(slide, 1.5, 4.2, 10, 0.8, "Cooperative Control of Multiple Unmanned Surface Vehicles\nUsing Multi-Agent Reinforcement Learning",
            18, LIGHT_BLUE, False, PP_ALIGN.CENTER)

# 하단 정보
shape = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE,
                                Inches(0), Inches(6.5), Inches(13.333), Inches(1))
shape.fill.solid()
shape.fill.fore_color.rgb = DARK_BLUE
shape.line.fill.background()
add_textbox(slide, 1.5, 6.6, 10, 0.7, "2026.03  |  ANSL",
            16, LIGHT_GRAY, False, PP_ALIGN.CENTER)

# ════════════════════════════════════════
# Slide 2: 목차
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)
add_textbox(slide, 0.8, 0.5, 5, 0.8, "목차", 36, WHITE, True)
add_accent_line(slide)

items = [
    ("01", "연구 배경 및 문제 정의"),
    ("02", "시스템 아키텍처"),
    ("03", "환경 설계 (시뮬레이션)"),
    ("04", "에이전트 설계 (관측 / 액션)"),
    ("05", "보상 함수 설계"),
    ("06", "Convoy-Deploy-Exit 메커니즘"),
    ("07", "커리큘럼 학습 전략"),
    ("08", "현재 진행 상황"),
    ("09", "향후 계획"),
]

for i, (num, title) in enumerate(items):
    y = 2.0 + i * 0.55
    add_textbox(slide, 2.0, y, 1, 0.5, num, 22, ACCENT_BLUE, True)
    add_textbox(slide, 3.0, y, 8, 0.5, title, 22, WHITE, False)


# ════════════════════════════════════════
# Slide 3: 연구 배경
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)
add_textbox(slide, 0.8, 0.5, 10, 0.8, "01  연구 배경 및 문제 정의", 32, WHITE, True)
add_accent_line(slide)

# 배경
box = add_shape_box(slide, 0.8, 1.8, 5.5, 2.5)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.2)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "연구 배경"
p.font.size = Pt(20)
p.font.color.rgb = ACCENT_BLUE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "• 해상 안보 위협 증가 (무인 자폭 수상정 등)",
    "• 다수 소형 무인 수상정(USV)의 협력 방어 필요",
    "• 기존 규칙 기반 제어의 한계 (동적 환경 대응 불가)",
    "• 강화학습(RL)을 통한 자율 협력 제어 연구 필요",
]:
    add_para(tf, line, 16, WHITE, False, space_before=Pt(8))

# 문제 정의
box = add_shape_box(slide, 7.0, 1.8, 5.5, 2.5)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.2)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "문제 정의"
p.font.size = Pt(20)
p.font.color.rgb = ORANGE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "• 2대의 방어 USV가 그물(Web)을 이용하여",
    "  다수의 적 공격선을 포획하는 협력 제어",
    "• 대형(Formation) 유지 + 전술 기동 동시 수행",
    "• 다수 쌍이 동시에 독립적으로 임무 수행",
]:
    add_para(tf, line, 16, WHITE, False, space_before=Pt(8))

# 핵심 도전과제
box = add_shape_box(slide, 0.8, 4.7, 11.7, 2.2)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.2)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "핵심 도전 과제"
p.font.size = Pt(20)
p.font.color.rgb = GREEN
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "① 실시간 협력 의사결정: 2대의 USV가 그물 간격을 유지하며 동시에 적을 추적",
    "② 가변적 적 수 대응: 에피소드마다 다른 적 수·포메이션에 대응하는 일반화 능력",
    "③ 다중 쌍 독립 운용: 여러 USV 쌍이 각각 독립적으로 적을 포획, 상호 충돌 회피",
    "④ 물리적 현실성: Gerstner 파도, 부력, 해류 등 실제 해양 환경 시뮬레이션",
]:
    add_para(tf, line, 15, LIGHT_GRAY, False, space_before=Pt(6))


# ════════════════════════════════════════
# Slide 4: 시스템 아키텍처
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)
add_textbox(slide, 0.8, 0.5, 10, 0.8, "02  시스템 아키텍처", 32, WHITE, True)
add_accent_line(slide)

# Unity 환경
box = add_shape_box(slide, 0.8, 1.8, 3.7, 4.8)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.15)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "Unity 시뮬레이션 환경"
p.font.size = Pt(18)
p.font.color.rgb = ACCENT_BLUE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "• Gerstner 파도 시뮬레이션",
    "• Rigidbody 기반 선박 물리",
    "• Engine.cs: 추진력/조타 제어",
    "• DynamicWeb: 그물 물리 시뮬레이션",
    "• 오브젝트 풀 시스템",
    "  (적군 동적 스폰/재활용)",
    "• 멀티 환경 병렬 학습 지원",
]:
    add_para(tf, line, 14, WHITE, False, space_before=Pt(5))

# ML-Agents
box = add_shape_box(slide, 4.8, 1.8, 3.7, 4.8)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.15)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "ML-Agents 프레임워크"
p.font.size = Pt(18)
p.font.color.rgb = ORANGE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "• MA-POCA (Multi-Agent POsthumous",
    "  Credit Assignment)",
    "• PPO (Proximal Policy Optimization)",
    "• DefenseAgent: 전술 에이전트",
    "• BufferSensor: 가변 적군 관측",
    "• GroupReward: 팀 보상 분배",
    "• DecisionRequester: 5 스텝 주기",
]:
    add_para(tf, line, 14, WHITE, False, space_before=Pt(5))

# 핵심 모듈
box = add_shape_box(slide, 8.8, 1.8, 3.7, 4.8)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.15)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "핵심 제어 모듈"
p.font.size = Pt(18)
p.font.color.rgb = GREEN
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "• DefenseEnvController",
    "  → 에피소드/보상 중앙 관리",
    "• DefenseRewardCalculator",
    "  → 보상 함수 모듈화",
    "• LaunchZoneManager",
    "  → 다수 쌍 진수/배치 관리",
    "• EnemyFormationSpawner",
    "  → 적 포메이션 (집중/파상/양동)",
]:
    add_para(tf, line, 14, WHITE, False, space_before=Pt(5))


# ════════════════════════════════════════
# Slide 5: 환경 설계
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)
add_textbox(slide, 0.8, 0.5, 10, 0.8, "03  환경 설계", 32, WHITE, True)
add_accent_line(slide)

# 환경 구성 요소
box = add_shape_box(slide, 0.8, 1.8, 5.8, 5.0)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.2)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "환경 구성 요소"
p.font.size = Pt(20)
p.font.color.rgb = ACCENT_BLUE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "모선 (MotherShip)",
    "  → 방어 대상, 중앙에 고정 배치",
    "",
    "방어 USV 쌍 (DefensePair)",
    "  → 2대가 그물로 연결, 다수 쌍 동시 운용",
    "  → LaunchZone에서 진수 (4방위: 0°/90°/180°/270°)",
    "",
    "적 공격선 (AttackBoat)",
    "  → 모선을 향해 돌진, 오브젝트 풀로 관리",
    "  → 3가지 포메이션: 집중/파상/양동 공격",
    "",
    "그물 (DynamicWeb)",
    "  → 2대 USV 사이에 동적으로 전개",
    "  → 최소 활성 거리(20m) 이상 시 활성화",
]:
    sz = 15 if line.startswith("  ") else 17
    b = not line.startswith("  ") and line != ""
    c = ORANGE if b else LIGHT_GRAY
    add_para(tf, line, sz, c, b, space_before=Pt(2))

# 적 포메이션
box = add_shape_box(slide, 7.0, 1.8, 5.5, 5.0)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.2)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "적 포메이션 시스템"
p.font.size = Pt(20)
p.font.color.rgb = ACCENT_BLUE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "",
    "① Concentrated (집중 공격)",
    "   → 한 방향 ±5~25° 밀집 돌진",
    "",
    "② Wave (파상 공격)",
    "   → 같은 방향 2~4 웨이브 시차 돌진",
    "",
    "③ Diversionary (양동 공격)",
    "   → 3방향 120° 간격 분산 공격",
    "",
    "④ Random (랜덤)",
    "   → 매 에피소드 3가지 중 랜덤 선택",
    "   → 과적합 방지",
]:
    sz = 15 if line.startswith("   ") else 16
    b = line.startswith("①") or line.startswith("②") or line.startswith("③") or line.startswith("④")
    c = GREEN if b else LIGHT_GRAY
    add_para(tf, line, sz, c, b, space_before=Pt(2))


# ════════════════════════════════════════
# Slide 6: 관측 공간
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)
add_textbox(slide, 0.8, 0.5, 10, 0.8, "04  에이전트 설계 — 관측 공간", 32, WHITE, True)
add_accent_line(slide)

# VectorSensor
box = add_shape_box(slide, 0.8, 1.8, 3.7, 4.5)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.15)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "VectorSensor (6개, ×3 Stack)"
p.font.size = Pt(16)
p.font.color.rgb = ACCENT_BLUE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "",
    "파트너 관측 (3개)",
    "  ① 거리: dist/(dist+k)",
    "  ② 방위: SignedBearing",
    "  ③ 헤딩차: sin(ΔAngle)",
    "",
    "모선 관측 (1개)",
    "  ④ 거리: dist/(dist+k)",
    "",
    "자기 상태 (2개)",
    "  ⑤ Throttle (0~1)",
    "  ⑥ Steering (-1~+1)",
]:
    sz = 13 if line.startswith("  ") else 15
    b = not line.startswith("  ") and line != ""
    c = ORANGE if b and not line.startswith("  ") else WHITE
    add_para(tf, line, sz, c, b, space_before=Pt(2))

# EnemyBufferSensor
box = add_shape_box(slide, 4.8, 1.8, 3.7, 4.5)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.15)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "EnemyBufferSensor"
p.font.size = Pt(16)
p.font.color.rgb = ACCENT_BLUE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "(최대 10대 × 4개 = 가변)",
    "",
    "적군 1대당 관측값:",
    "  ① Dist: 정규화 거리",
    "  ② SignedBrg: 상대 방위각",
    "  ③ Hdg: 헤딩 차이",
    "  ④ SignedRayDist:",
    "     적→모선 Ray 기준",
    "     좌(-)/우(+) 수직 거리",
    "",
    "※ 거리순 정렬",
    "※ Neutralized 적 제외",
]:
    sz = 13 if line.startswith("  ") or line.startswith("     ") else 14
    b = line.startswith("적군") or line.startswith("※")
    c = ORANGE if line.startswith("적군") else GREEN if line.startswith("※") else WHITE
    add_para(tf, line, sz, c, b, space_before=Pt(2))

# AllyBufferSensor
box = add_shape_box(slide, 8.8, 1.8, 3.7, 4.5)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.15)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "AllyBufferSensor"
p.font.size = Pt(16)
p.font.color.rgb = ACCENT_BLUE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "(최대 10쌍 × 3개 = 가변)",
    "",
    "아군 쌍 1개당 관측값:",
    "  ① Dist: 쌍 중심까지 거리",
    "  ② Bearing: 상대 방위",
    "  ③ WebLength: 그물 길이",
    "",
    "※ 거리순 정렬",
    "※ 트랩(정지 그물) 포함",
    "",
    "핵심 설계 원칙:",
    "  모든 관측값은 자기 기준",
    "  상대값 (절대 좌표 미사용)",
]:
    sz = 13 if line.startswith("  ") else 14
    b = line.startswith("아군") or line.startswith("핵심") or line.startswith("※")
    c = ORANGE if line.startswith("아군") or line.startswith("핵심") else GREEN if line.startswith("※") else WHITE
    add_para(tf, line, sz, c, b, space_before=Pt(2))

# 총 관측 차원
add_textbox(slide, 0.8, 6.5, 12, 0.5,
            "총 관측 차원:  VectorSensor 6×3(Stack) = 18  +  EnemyBuffer 4×10(max)  +  AllyBuffer 3×10(max)  =  가변 (최대 88차원)",
            14, LIGHT_BLUE, True, PP_ALIGN.CENTER)


# ════════════════════════════════════════
# Slide 7: 액션 공간
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)
add_textbox(slide, 0.8, 0.5, 10, 0.8, "04  에이전트 설계 — 액션 공간", 32, WHITE, True)
add_accent_line(slide)

box = add_shape_box(slide, 1.5, 1.8, 10.3, 2.5)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.3)
tf.margin_top = Inches(0.2)
p = tf.paragraphs[0]
p.text = "연속 액션 2개 (Continuous Actions)"
p.font.size = Pt(22)
p.font.color.rgb = ACCENT_BLUE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "",
    "Action[0] — Throttle (추력):   -1 ~ +1  →  매핑: (input+1)×0.25 + 0.5  =  0.5 ~ 1.0",
    "Action[1] — Steering (조타):   -1 ~ +1  →  감도 계수(0.3) 적용 후 Engine.Turn() 전달",
]:
    add_para(tf, line, 16, WHITE, False, space_before=Pt(8))

# 제어 특성
box = add_shape_box(slide, 1.5, 4.7, 5.0, 2.2)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.2)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "물리 제어 특성"
p.font.size = Pt(18)
p.font.color.rgb = ORANGE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "• ForceMode.Acceleration (질량 무관)",
    "• Gerstner 파도와 연동된 부력 시스템",
    "• 최소 속도 50% 보장 (후진 불가)",
    "• 입력 스무딩: Lerp 기반 부드러운 전환",
]:
    add_para(tf, line, 14, LIGHT_GRAY, False, space_before=Pt(5))

box = add_shape_box(slide, 6.8, 4.7, 5.0, 2.2)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.2)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "DecisionRequester 설정"
p.font.size = Pt(18)
p.font.color.rgb = GREEN
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "• Decision Period: 5 (5 FixedUpdate마다)",
    "• TakeActionsBetweenDecisions: True",
    "• time_scale: 10 (물리 안정성 확보)",
    "• 실효 결정 주기: ~0.5초 (실시간 환산)",
]:
    add_para(tf, line, 14, LIGHT_GRAY, False, space_before=Pt(5))


# ════════════════════════════════════════
# Slide 8: 보상 함수
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)
add_textbox(slide, 0.8, 0.5, 10, 0.8, "05  보상 함수 설계", 32, WHITE, True)
add_accent_line(slide)

# Dense 보상
box = add_shape_box(slide, 0.8, 1.8, 5.8, 2.8)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.2)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "Dense 보상 (매 스텝)"
p.font.size = Pt(20)
p.font.color.rgb = GREEN
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "",
    "① 대형 유지: +0.005 × (1 - error/tolerance)",
    "   → 아군 간격 50m±35m 범위 내 보상",
    "② 대형 이탈 페널티: -0.002 × 초과거리(m)",
    "   → 범위 밖 연속 페널티 (Soft Boundary)",
    "③ Ray 수직 접근: +0.002 × (1 - d/(d+200))",
    "   → 적→모선 Ray에 가까울수록 보상",
    "④ 추력 보상: +0.0002 × throttle",
]:
    sz = 14 if line.startswith("   ") else 15
    add_para(tf, line, sz, WHITE if not line.startswith("   ") else LIGHT_GRAY,
             False, space_before=Pt(3))

# Sparse 보상
box = add_shape_box(slide, 7.0, 1.8, 5.5, 2.8)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.2)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "Sparse 보상 (이벤트)"
p.font.size = Pt(20)
p.font.color.rgb = ORANGE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "",
    "⊕ 포획 성공:   +1.0 (연속 보너스 포함)",
    "⊕ 전멸 보너스: +2.0",
    "⊖ 모선 충돌:   -1.0",
    "⊖ 적 돌파:     -1.0",
    "⊖ 아군 충돌:   -0.5",
    "⊖ 거리 초과:   -0.5 + 에피소드 종료",
    "⊖ 잔여 적:     -0.5 × 남은 적 수",
]:
    c = GREEN if line.startswith("⊕") else RED if line.startswith("⊖") else WHITE
    add_para(tf, line, 15, c, False, space_before=Pt(3))

# 보상 설계 원칙
box = add_shape_box(slide, 0.8, 5.0, 11.7, 1.8)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.2)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "보상 설계 원칙"
p.font.size = Pt(18)
p.font.color.rgb = ACCENT_BLUE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "• Soft Boundary: 대형 이탈 시 연속 페널티 → Hard Kill(에피소드 종료) 전에 교정 기회 제공",
    "• 관측-보상 커플링: SignedRayDist 관측값과 Ray 접근 보상이 동일 기하학 기반 → 학습 가속",
    "• Dense 보상 우선: 매 스텝 작은 보상이 Sparse 이벤트 보상보다 학습에 효과적",
]:
    add_para(tf, line, 14, LIGHT_GRAY, False, space_before=Pt(5))


# ════════════════════════════════════════
# Slide 9: Convoy-Deploy-Exit
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)
add_textbox(slide, 0.8, 0.5, 10, 0.8, "06  Convoy-Deploy-Exit 메커니즘", 32, WHITE, True)
add_accent_line(slide)

# Phase 1: Convoy
box = add_shape_box(slide, 0.8, 1.8, 3.7, 4.5)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.15)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "CONVOY (접근)"
p.font.size = Pt(20)
p.font.color.rgb = ACCENT_BLUE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "",
    "• 2대가 대형 유지하며 이동",
    "• 그물 접힌 상태 (< 20m)",
    "• ML 정책으로 조향/추력 제어",
    "• 적→모선 Ray 기준 접근",
    "",
    "활성 보상:",
    "  ✓ 대형 유지 (+0.005)",
    "  ✓ Ray 수직 접근 (+0.002)",
    "  ✓ 추력 보상",
]:
    sz = 14
    b = line.startswith("활성")
    c = GREEN if line.startswith("  ✓") else ORANGE if b else WHITE
    add_para(tf, line, sz, c, b, space_before=Pt(3))

# Phase 2: Deploy
box = add_shape_box(slide, 4.8, 1.8, 3.7, 4.5)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.15)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "DEPLOY (전개)"
p.font.size = Pt(20)
p.font.color.rgb = ORANGE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "",
    "• 적 150m 이내 진입 시 트리거",
    "• 양쪽으로 분리 조향",
    "• 그물 자동 전개 (> 20m)",
    "• 목표 간격 도달까지 확장",
    "",
    "전환 조건:",
    "  → deployRange: 150m",
    "  → 규칙 기반 (ML 아님)",
    "  → deploySplitSteer로 분리",
]:
    sz = 14
    b = line.startswith("전환")
    c = ACCENT_BLUE if line.startswith("  →") else ORANGE if b else WHITE
    add_para(tf, line, sz, c, b, space_before=Pt(3))

# Phase 3: Exit
box = add_shape_box(slide, 8.8, 1.8, 3.7, 4.5)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.15)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "EXIT (정지/포획)"
p.font.size = Pt(20)
p.font.color.rgb = GREEN
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "",
    "• 간격 100m+ 도달 시 정지",
    "• 선박 Neutralized (ML 비활성)",
    "• 그물 유지 → 다중 포획 가능",
    "• 파도/해류에 자연 표류",
    "",
    "핵심 특징:",
    "  → 한번 전개된 그물 회수 불가",
    "  → 에피소드 종료까지 유지",
    "  → 새 쌍이 추가 진수 가능",
]:
    sz = 14
    b = line.startswith("핵심")
    c = ACCENT_BLUE if line.startswith("  →") else GREEN if b else WHITE
    add_para(tf, line, sz, c, b, space_before=Pt(3))


# ════════════════════════════════════════
# Slide 10: 커리큘럼 학습
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)
add_textbox(slide, 0.8, 0.5, 10, 0.8, "07  커리큘럼 학습 전략", 32, WHITE, True)
add_accent_line(slide)

stages = [
    ("Stage 1", "Formation", "대형 유지 학습",
     "• 적군 없이 대형 유지만 학습\n• 간격 50m±35m 보상\n• 기본 조향/추력 익힘", ACCENT_BLUE),
    ("Stage 2", "Capture", "포획 학습",
     "• 적군 등장, 포획 보상 활성\n• Ray 수직 접근 학습\n• Web 전개 타이밍 학습", ORANGE),
    ("Stage 3", "Tactical", "종합 전술",
     "• Stage 1+2 모든 보상 활성\n• 대형 유지 + 포획 동시 수행\n• 다수 적 포메이션 대응", GREEN),
    ("Stage 8", "FullObs", "전체 관측 전술",
     "• BufferSensor 전체 적 관측\n• 자율 적 분담 (타겟 배정 X)\n• 다수 쌍 독립 운용", RED),
]

for i, (num, name, desc, details, color) in enumerate(stages):
    x = 0.8 + i * 3.1
    box = add_shape_box(slide, x, 1.8, 2.8, 4.8)
    tf = box.text_frame
    tf.word_wrap = True
    tf.margin_left = Inches(0.12)
    tf.margin_top = Inches(0.12)
    p = tf.paragraphs[0]
    p.text = num
    p.font.size = Pt(14)
    p.font.color.rgb = color
    p.font.bold = True
    p.font.name = "맑은 고딕"

    add_para(tf, name, 20, color, True, space_before=Pt(2))
    add_para(tf, desc, 14, WHITE, False, space_before=Pt(8))

    for line in details.split("\n"):
        add_para(tf, line, 13, LIGHT_GRAY, False, space_before=Pt(4))

    # 화살표
    if i < 3:
        ax = x + 2.9
        add_textbox(slide, ax, 3.8, 0.3, 0.5, "→", 24, ACCENT_BLUE, True, PP_ALIGN.CENTER)


# ════════════════════════════════════════
# Slide 11: 현재 진행 상황
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)
add_textbox(slide, 0.8, 0.5, 10, 0.8, "08  현재 진행 상황", 32, WHITE, True)
add_accent_line(slide)

# 완료 항목
box = add_shape_box(slide, 0.8, 1.8, 5.8, 4.8)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.2)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "완료된 구현"
p.font.size = Pt(20)
p.font.color.rgb = GREEN
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "",
    "✅ Unity 시뮬레이션 환경 구축",
    "   Gerstner 파도, 선박 물리, 부력 시스템",
    "✅ DefenseAgent 관측/액션 설계",
    "   VectorSensor(6) + BufferSensor(Enemy/Ally)",
    "✅ 보상 함수 모듈화 (DefenseRewardCalculator)",
    "   대형 유지 + Ray 접근 + 포획/충돌 이벤트",
    "✅ 오브젝트 풀 시스템 (적군 동적 스폰)",
    "✅ 적군 포메이션 시스템 (3가지 패턴)",
    "✅ 다수 쌍 진수/관리 시스템 (LaunchZoneManager)",
    "✅ Convoy-Deploy-Exit 메커니즘",
    "✅ 커리큘럼 학습 Stage 시스템",
]:
    sz = 13 if line.startswith("   ") else 15
    c = GREEN if line.startswith("✅") else LIGHT_GRAY
    add_para(tf, line, sz, c, line.startswith("✅"), space_before=Pt(3))

# 진행 중
box = add_shape_box(slide, 7.0, 1.8, 5.5, 4.8)
tf = box.text_frame
tf.word_wrap = True
tf.margin_left = Inches(0.2)
tf.margin_top = Inches(0.15)
p = tf.paragraphs[0]
p.text = "진행 중 / 개선 필요"
p.font.size = Pt(20)
p.font.color.rgb = ORANGE
p.font.bold = True
p.font.name = "맑은 고딕"

for line in [
    "",
    "🔄 대형 유지 학습 안정화",
    "   보상 강화(×5) + Soft Boundary 적용",
    "🔄 보상 밸런싱 튜닝",
    "   Dense vs Sparse 보상 비율 최적화",
    "🔄 하이퍼파라미터 튜닝",
    "   batch_size, buffer_size, beta 조정",
    "",
    "🔬 실험 중인 접근법",
    "   • FixedJoint 쌍동선 (물리 연결)",
    "     → 파도 비대칭 힘 문제로 보류",
    "   • 공격선 ML 학습 (Self-Play 준비)",
    "     → 별도 브랜치에서 개발 중",
]:
    sz = 13 if line.startswith("   ") else 15
    b = line.startswith("🔄") or line.startswith("🔬")
    c = ORANGE if line.startswith("🔄") else ACCENT_BLUE if line.startswith("🔬") else LIGHT_GRAY
    add_para(tf, line, sz, c, b, space_before=Pt(3))


# ════════════════════════════════════════
# Slide 12: 향후 계획
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)
add_textbox(slide, 0.8, 0.5, 10, 0.8, "09  향후 계획", 32, WHITE, True)
add_accent_line(slide)

plans = [
    ("Phase 1", "학습 안정화", "3~4월",
     ["대형 유지 학습 수렴 달성",
      "보상 함수 밸런싱 완료",
      "Stage 1~3 커리큘럼 검증"], ACCENT_BLUE),
    ("Phase 2", "다중 쌍 운용", "5~6월",
     ["Stage 8: 다수 쌍 독립 기동",
      "쌍 간 충돌 회피 학습",
      "적 자율 분담 능력 검증"], ORANGE),
    ("Phase 3", "Self-Play", "7~8월",
     ["공격선 ML 에이전트 통합",
      "방어 vs 공격 동시 학습",
      "적응적 전술 창발 관찰"], GREEN),
    ("Phase 4", "논문 작성", "9~10월",
     ["실험 결과 정리/분석",
      "기존 연구 대비 성능 비교",
      "논문 투고"], RED),
]

for i, (phase, title, period, items, color) in enumerate(plans):
    x = 0.8 + i * 3.1
    box = add_shape_box(slide, x, 1.8, 2.8, 4.5)
    tf = box.text_frame
    tf.word_wrap = True
    tf.margin_left = Inches(0.12)
    tf.margin_top = Inches(0.12)
    p = tf.paragraphs[0]
    p.text = phase
    p.font.size = Pt(14)
    p.font.color.rgb = color
    p.font.bold = True
    p.font.name = "맑은 고딕"

    add_para(tf, title, 20, color, True, space_before=Pt(2))
    add_para(tf, period, 14, LIGHT_GRAY, False, space_before=Pt(4))
    add_para(tf, "", 8, WHITE, False, space_before=Pt(4))

    for item in items:
        add_para(tf, f"• {item}", 14, WHITE, False, space_before=Pt(6))

    if i < 3:
        ax = x + 2.9
        add_textbox(slide, ax, 3.5, 0.3, 0.5, "→", 24, ACCENT_BLUE, True, PP_ALIGN.CENTER)


# ════════════════════════════════════════
# Slide 13: Q&A
# ════════════════════════════════════════
slide = prs.slides.add_slide(prs.slide_layouts[6])
add_bg(slide)

shape = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE,
                                Inches(0), Inches(0), Inches(13.333), Inches(0.08))
shape.fill.solid()
shape.fill.fore_color.rgb = ACCENT_BLUE
shape.line.fill.background()

add_textbox(slide, 1.5, 2.5, 10, 1.5, "Q & A", 60, WHITE, True, PP_ALIGN.CENTER)
add_textbox(slide, 1.5, 4.2, 10, 0.8, "감사합니다", 28, LIGHT_BLUE, False, PP_ALIGN.CENTER)

shape = slide.shapes.add_shape(MSO_SHAPE.RECTANGLE,
                                Inches(0), Inches(6.5), Inches(13.333), Inches(1))
shape.fill.solid()
shape.fill.fore_color.rgb = DARK_BLUE
shape.line.fill.background()
add_textbox(slide, 1.5, 6.6, 10, 0.7, "강화학습 기반 다중 무인 수상정 협력 제어  |  중간 보고  |  2026.03",
            14, LIGHT_GRAY, False, PP_ALIGN.CENTER)


# ── 저장 ──
output_path = os.path.join(os.path.dirname(__file__), "중간보고_다중USV협력제어.pptx")
prs.save(output_path)
print(f"PPT 생성 완료: {output_path}")
