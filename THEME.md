# 艾莉丝 · 冒险手账主题

版本：v2.2.0。默认开启，可在右键菜单“气泡外观”切换为简洁米白。主题选择保存在本机 settings.json。

## 设计与使用

- 背景采用艾莉丝、异世界城镇、旅途地图和羊皮纸意象，搭配赤红、暖金与棕色文字。
- 插画只放在气泡上部，按照原始比例绘制；信息区域保留浅底，避免头像被拉长或压住文字。
- 额度以圆角卡片呈现，完成提醒采用时间线卡片；按钮与倒计时细线统一配色。
- 气泡轻微淡入淡出；系统关闭菜单动画时省略淡入淡出。
- 保留 8 秒收起、悬停暂停和读取结束后再计时。右上角关闭按钮始终可用。
- 图片缺失或损坏时回退到浅色底，语音、动作和通知继续工作。

## 参考来源

仅参考以下项目的公开设计与交互说明，没有复制它们的代码或美术资源。

- [TonyNa-code/desktop-pet](https://github.com/TonyNa-code/desktop-pet)：跟随角色的气泡、悬停暂停与根据内容控制停留时间。
- [valerieliang/desktop-pet](https://github.com/valerieliang/desktop-pet)：可替换的气泡背景、边角与中间区域分开处理、图片缺失时的简单形状回退。
- [《无职转生》动画官方角色页](https://mushokutensei.jp/character/)：艾莉丝的角色与冒险主题参考。

## 插画来源与生成记录

文件：`bubble-eris-adventure.png`。通过内置 imagegen 工具生成的同人主题插画，非动画截图或官方美术。生成后原样复制进项目，没有改动既有桌宠图集。角色相关权利归原权利人；沿用项目的非官方同人说明。

生成提示词（原文）：

> Use case: stylized-concept. Asset type: a production background banner for an Eris desktop pet speech bubble UI. Primary request: Mushoku Tensei / Jobless Reincarnation themed, featuring Eris Boreas Greyrat as a tasteful fan illustration, not an official screenshot. Create a very wide 3:1 landscape banner, ideally 1536x512. Style: polished hand painted anime travel-journal illustration, fine ink and warm watercolor on subtle ivory parchment, refined rather than childish. Composition: left 65% is extremely quiet pale cream parchment negative space, almost blank for dark UI text. In the rightmost 30%, Eris is recognizable by long flowing crimson-red hair, red eyes, white high-collar blouse, modest brown leather adventurer vest and travelling clothes, a tiny hint of a sheathed sword, calm confident friendly expression. Bust portrait cropped at bottom, face fully within upper right quarter with safe inset from top and right edges. A very faint medieval Asura-like town with terracotta rooftops and distant rolling hills blends into cream behind her, and minimal gold map route/compass motifs at far outer edges only. Lighting: soft sunset amber. Palette: warm ivory #FFF8EB, oxblood crimson #8E3842, muted antique gold #B58B54, sepia ink. Keep overall background very light and low contrast; her portrait should remain readable as an accent at 350px width, never dominate left text area. No text, no lettering, no fake Japanese, no logos, no watermark, no UI controls, no visible frame, no speech-bubble shape. Fill entire rectangular canvas, not transparency. Make it beautiful as a compact fantasy RPG companion interface header.

界面预览为程序控件的离屏渲染，额度、提醒时间等使用示例数据。

