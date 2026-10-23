namespace WorldBirdLauncher.App.Views;

/// <summary>
/// 皮肤页 WebView2 里那张页面的 HTML（从 SkinPage.xaml.cs 里拆出来，单独一份便于维护）。
///
/// ★ 2026-10-08 改（用户反馈"先白屏、loading 只闪 0.1 秒"）：
/// 页面**不再把皮肤写死在 HTML 里**，而是：
///   ① 进页面立刻导航这张"loading 页"（第一眼就是转圈）；
///   ② 页面把监听器装好后 postMessage('page-ready')；
///   ③ C# 把皮肤用 postMessage 推过来（{type:'skin',url,slim}）→ 页面才创建 skinview3d 并加载模型；
///   ④ 模型首帧渲染完成后再淡出转圈，同时 postMessage('skin-ready') 给 C# 记日志。
/// 切换"深夜网格 / 天空草地"也走消息（{type:'background'}），**不重新导航、不重载模型**。
/// 两套背景层都写在页面里，用 body 上的 `bg-sky` 类切换。
/// </summary>
internal static class SkinPageHtml
{
    public static string Build(string background, string skinViewScript)
    {
        var isSky = background == "sky";

        return $$"""
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8" />
  <style>
    html, body { margin:0; padding:0; height:100%; overflow:hidden; }
    /* 两套背景配色都用 CSS 变量：body.bg-sky 一挂上去就整体切换 */
    body {
      --loading-bg:#1b1c1f; --tip:#9aa0a6; --spinner-track:rgba(120,150,220,.25); --text:#9aa0a6;
      background:linear-gradient(180deg,#1b1c1f 0%,#232529 55%,#2b2d31 100%);
    }
    body.bg-sky {
      --loading-bg:#d8ecff; --tip:#3d4a5c; --spinner-track:rgba(43,127,255,.25); --text:#3d4a5c;
      background:linear-gradient(180deg,#6fa8ff 0%,#a9cdff 52%,#d8ecff 100%);
    }
    #stage { position:relative; width:100vw; height:100vh; overflow:hidden; }
    /* 明确层级：背景层(0) < 3D 画布(1) < 提示文字(2)，否则 WebView2 的合成层可能盖住模型 */
    #view { position:absolute; inset:0; width:100%; height:100%; display:block; z-index:1; }

    /* 深夜网格：远处淡出的透视网格 */
    #grid { position:absolute; left:-30%; right:-30%; bottom:-4%; height:44%;
            z-index:0;
            background-image:linear-gradient(rgba(175,200,245,.55) 1px, transparent 1px),
                             linear-gradient(90deg, rgba(175,200,245,.55) 1px, transparent 1px);
            background-size:64px 64px;
            transform:perspective(320px) rotateX(63deg); transform-origin:bottom center;
            -webkit-mask-image:linear-gradient(to top, rgba(0,0,0,1) 0%, rgba(0,0,0,.45) 55%, transparent 94%);
            mask-image:linear-gradient(to top, rgba(0,0,0,1) 0%, rgba(0,0,0,.45) 55%, transparent 94%); }
    #stars { position:absolute; inset:0; pointer-events:none; z-index:0; }
    .star { position:absolute; width:2px; height:2px; border-radius:50%; background:#fff;
            opacity:.5; animation:twinkle 3.4s infinite ease-in-out; }
    @keyframes twinkle { 0%,100% { opacity:.18; } 50% { opacity:.9; } }

    /* 天空草地：方块草皮 + 脚下阴影 */
    #sky { position:absolute; left:-25%; right:-25%; top:-8%; bottom:-8%; z-index:0;
           background-image:linear-gradient(180deg,#6fa8ff 0%,#a9cdff 52%,#d8ecff 100%);
           background-size:200% 100%; background-position-x:0; }
    #ground { position:absolute; left:-55%; right:-55%; bottom:0; height:32%;
              z-index:0;
              background-color:#7cb85c;
              background-image:linear-gradient(rgba(0,0,0,.16) 1px, transparent 1px),
                               linear-gradient(90deg, rgba(0,0,0,.16) 1px, transparent 1px);
              background-size:56px 56px;
              transform:perspective(340px) rotateX(64deg); transform-origin:bottom center; }
    #shadow { position:absolute; left:50%; top:88%; width:190px; height:46px; margin-left:-95px; margin-top:-23px;
              z-index:0;
              border-radius:50%; background:radial-gradient(closest-side, rgba(0,0,0,.5), rgba(0,0,0,0));
              pointer-events:none; }

    /* 两套背景层常驻，靠 body 的 bg-sky 类切换（切换时不用重新导航） */
    #sky, #ground, #shadow { display:none; }
    body.bg-sky #sky, body.bg-sky #ground, body.bg-sky #shadow { display:block; }
    #grid, #stars { display:none; }
    body:not(.bg-sky) #grid, body:not(.bg-sky) #stars { display:block; }

    #tip { position:absolute; left:12px; bottom:10px; color:var(--tip);
           z-index:2; font:12px "Microsoft YaHei",sans-serif; }

    /* loading 层：模型第一帧画出来之前盖住画布。
       放在页面里而不是 WPF 层：WebView2 是 HWND 承载的，WPF 控件盖不到它上面（airspace）。 */
    #loading { position:absolute; inset:0; z-index:5; display:flex; flex-direction:column;
               align-items:center; justify-content:center; gap:12px;
               background:var(--loading-bg);
               transition:opacity .3s ease; }
    #loading.done { opacity:0; pointer-events:none; }
    #loading .spinner { width:34px; height:34px; border-radius:50%;
               border:3px solid var(--spinner-track);
               border-top-color:#2B7FFF; animation:spin .9s linear infinite; }
    #loading .text { font:12px "Microsoft YaHei",sans-serif; color:var(--text); }
    @keyframes spin { to { transform:rotate(360deg); } }
  </style>
</head>
<body class="{{(isSky ? "bg-sky" : string.Empty)}}">
  <div id="stage">
    <div id="sky"></div>
    <div id="ground"></div>
    <div id="shadow"></div>
    <div id="grid"></div>
    <div id="stars"></div>
    <canvas id="view"></canvas>
    <div id="tip">按住鼠标左键拖动可旋转，滚轮缩放</div>
    <div id="loading">
      <div class="spinner"></div>
      <div class="text" id="loading-text">正在准备 3D 视图…</div>
    </div>
  </div>
  <script>{{skinViewScript}}</script>
  <script>
    (function () {
      const bridge = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;

      // ---- 背景切换（页内切，不重新导航）----
      const bg = { isSky: {{(isSky ? "true" : "false")}} };
      function setBackground(name) {
        bg.isSky = (name === 'sky');
        document.body.classList.toggle('bg-sky', bg.isSky);
      }

      // ---- loading 控制 ----
      const loadingEl = document.getElementById('loading');
      const loadingText = document.getElementById('loading-text');
      let loadingHidden = false;
      function hideLoading() {
        if (loadingHidden) { return; }
        loadingHidden = true;
        if (loadingEl) {
          loadingEl.classList.add('done');
          setTimeout(function () { if (loadingEl.parentNode) { loadingEl.parentNode.removeChild(loadingEl); } }, 320);
        }
        // 通知父窗口（C# 侧记一行日志，方便排查"一直转圈"）
        try { if (bridge) { bridge.postMessage('skin-ready'); } } catch (err) { }
      }

      let viewer = null;

      // ---- 建 3D 视图（等 C# 把皮肤推过来才走这里）----
      function startViewer(skinUrl, isSlim) {
        try {
          const canvas = document.getElementById('view');
          viewer = new skinview3d.SkinViewer({
            canvas: canvas,
            width: window.innerWidth,
            height: window.innerHeight,
            skin: skinUrl,
            model: isSlim ? 'slim' : 'auto'
          });
          viewer.animation = new skinview3d.WalkingAnimation();
          viewer.animation.speed = 0.6;
          viewer.controls.enableZoom = true;

          // 模型第一帧真正画出来（playerObject 里已经有网格）再撤 loading；
          // 最多等 8 秒兜底，避免异常情况下一直转圈。
          let bootTries = 0;
          (function waitForModel() {
            bootTries++;
            const player = viewer.playerObject;
            const hasMesh = !!(player && player.children && player.children.length > 0);
            if (hasMesh) {
              // 等两帧，确保已经合成到画布上
              requestAnimationFrame(function () { requestAnimationFrame(hideLoading); });
              return;
            }

            if (bootTries > 80) { hideLoading(); return; }
            setTimeout(waitForModel, 100);
          })();

          // 深夜网格的星点（只在没有星点时建一次）
          const stars = document.getElementById('stars');
          if (stars && stars.childElementCount === 0) {
            for (let i = 0; i < 110; i++) {
              const star = document.createElement('div');
              star.className = 'star';
              star.style.left = (Math.random() * 100).toFixed(2) + '%';
              star.style.top = (Math.random() * 62).toFixed(2) + '%';
              star.style.animationDelay = (Math.random() * 3.4).toFixed(2) + 's';
              star.style.opacity = (0.2 + Math.random() * 0.6).toFixed(2);
              stars.appendChild(star);
            }
          }

          // 背景跟随拖拽旋转：地面同向转动，星点/天空做视差位移
          const shadow = document.getElementById('shadow');
          const ground = document.getElementById('ground') || document.getElementById('grid');
          const starsLayer = document.getElementById('stars');
          const skyLayer = document.getElementById('sky');
          function syncShadow() {
            if (shadow && viewer && viewer.camera) {
              const Vector3 = viewer.camera.position.constructor;
              let feetY = -12;
              const player = viewer.playerObject;
              if (player && player.traverse) {
                let lowest = Infinity;
                player.traverse(function (o) {
                  if (!o.isMesh || !o.geometry) { return; }
                  if (!o.geometry.boundingBox) { o.geometry.computeBoundingBox(); }
                  const bb = o.geometry.boundingBox;
                  if (!bb) { return; }
                  const corners = [
                    [bb.min.x, bb.min.y, bb.min.z], [bb.max.x, bb.min.y, bb.min.z],
                    [bb.min.x, bb.min.y, bb.max.z], [bb.max.x, bb.min.y, bb.max.z]
                  ];
                  for (let i = 0; i < corners.length; i++) {
                    const p = new Vector3(corners[i][0], corners[i][1], corners[i][2]);
                    o.localToWorld(p);
                    if (p.y < lowest) { lowest = p.y; }
                  }
                });
                if (lowest < Infinity) { feetY = lowest; }
              }
              const feet = new Vector3(0, feetY, 0);
              feet.project(viewer.camera);
              const rect = canvas.getBoundingClientRect();
              shadow.style.left = ((feet.x * 0.5 + 0.5) * rect.width).toFixed(1) + 'px';
              shadow.style.top = ((-feet.y * 0.5 + 0.5) * rect.height).toFixed(1) + 'px';
            }

            if (viewer && viewer.playerWrapper) {
              const degrees = viewer.playerWrapper.rotation.y * 180 / Math.PI;
              if (ground) {
                ground.style.transform =
                  'perspective(' + (bg.isSky ? 340 : 320) + 'px) rotateX(' + (bg.isSky ? 64 : 63) + 'deg) rotateZ(' +
                  degrees.toFixed(2) + 'deg)';
              }
              if (starsLayer && !bg.isSky) {
                starsLayer.style.transform =
                  'rotate(' + (degrees * 0.35).toFixed(2) + 'deg) scale(1.6) translateX(' + (degrees * 1.1).toFixed(1) + 'px)';
              }
              if (skyLayer && bg.isSky) {
                skyLayer.style.backgroundPositionX = (degrees * 2.2).toFixed(1) + 'px';
              }
            }
            window.requestAnimationFrame(syncShadow);
          }
          window.requestAnimationFrame(syncShadow);

          window.addEventListener('resize', function () { viewer.setSize(window.innerWidth, window.innerHeight); });
        } catch (e) {
          document.getElementById('tip').textContent = '3D 渲染失败：' + e.message + '（脚本未加载成功？）';
          hideLoading();
        }
      }

      // ---- 接收 C# 的推送 ----
      if (bridge) {
        bridge.addEventListener('message', function (ev) {
          let data = ev.data;
          if (typeof data === 'string') {
            try { data = JSON.parse(data); } catch (e) { return; }
          }
          if (!data || !data.type) { return; }

          if (data.type === 'background') {
            setBackground(data.background);
            return;
          }

          if (data.type === 'skin') {
            if (loadingText) { loadingText.textContent = '正在加载 3D 模型…'; }
            startViewer(data.url, !!data.slim);
          }
        });
      }

      // 监听器装好了：让 C# 放心推皮肤（发早了会丢）
      try { if (bridge) { bridge.postMessage('page-ready'); } } catch (err) { }
    })();
  </script>
</body>
</html>
""";
    }
}
