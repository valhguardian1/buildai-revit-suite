/*
 * BuildAI — Viewer-side anchor probe
 *
 * Использует только те API, которые подтверждены документацией APS
 * или рабочими примерами Autodesk:
 *   viewer.impl.hitTest(x, y, ignoreTransparent) -> {intersectPoint, dbId}
 *   model.getData().globalOffset
 *   ViewerCoord + globalOffset = координата модели, без поворота
 *
 * Приватных вызовов с неподтверждённой сигнатурой здесь нет.
 *
 * ДВЕ ТОЧКИ ВХОДА
 *   detectFrame(viewer, referencePins) -> { frame, confidence, votes, note }
 *   probeBatch(viewer, { requests: [...] }) -> Promise<[AnchorResult]>
 */

(function (global) {
  'use strict';

  var THREE = global.THREE;

  var QUALITY = {
    SNAPPED: 'Snapped',        // точка снята с грани через hitTest
    BBOX: 'BBoxCentre',        // hitTest не попал, деградированный якорь
    NOTFOUND: 'NotFound'       // элемент не разрешился в текущем derivative
  };

  var FRAME = { VIEWER: 'ViewerLocal', MODEL: 'Model' };

  // Отступ пина наружу из грани, в футах. Пересчитывается по единицам
  // модели: у Revit-деривативов мировая единица — фут, но полагаться на
  // это вслепую нельзя, иначе в метровой модели отступ вырастет втрое.
  var PIN_OFFSET_FT = 0.25;    // ~75 мм
  var FEET_PER_METRE = 3.280839895;

  // ---------------------------------------------------------------- utils

  function vec(o) { return new THREE.Vector3(o.x, o.y, o.z); }
  function out(v) { return { x: v.x, y: v.y, z: v.z }; }

  function shift(v, g, sign) {
    return { x: v.x + sign * g.x, y: v.y + sign * g.y, z: v.z + sign * g.z };
  }

  function nextFrame() {
    return new Promise(function (resolve) {
      if (global.requestAnimationFrame) global.requestAnimationFrame(function () { resolve(); });
      else global.setTimeout(resolve, 16);
    });
  }

  function delay(ms) {
    return new Promise(function (resolve) { global.setTimeout(resolve, ms); });
  }

  /*
   * Сколько мировых единиц в одном футе. model.getUnitScale() отдаёт метры
   * на единицу; если метод недоступен или вернул мусор, считаем единицу
   * футом — так вело себя всё, что работало до сих пор.
   */
  function feetPerUnit(model) {
    try {
      var metresPerUnit = model.getUnitScale();
      if (typeof metresPerUnit === 'number' && isFinite(metresPerUnit) && metresPerUnit > 0)
        return metresPerUnit * FEET_PER_METRE;
    } catch (e) { /* единица считается футом */ }
    return 1;
  }

  function distanceToBox(p, box) {
    var dx = Math.max(box.min.x - p.x, 0, p.x - box.max.x);
    var dy = Math.max(box.min.y - p.y, 0, p.y - box.max.y);
    var dz = Math.max(box.min.z - p.z, 0, p.z - box.max.z);
    return Math.sqrt(dx * dx + dy * dy + dz * dz);
  }

  function boxIsUsable(box) {
    // Проверки пустоты у Box3 называются по-разному между ревизиями THREE,
    // которые Autodesk кладёт во вьюер, и вызов отсутствующей уронил бы
    // обход фрагментов целиком. Проверка на конечность от ревизии не зависит.
    var v = [box.min.x, box.min.y, box.min.z, box.max.x, box.max.y, box.max.z];
    for (var i = 0; i < v.length; i++) if (!isFinite(v[i])) return false;
    return box.max.x >= box.min.x && box.max.y >= box.min.y && box.max.z >= box.min.z;
  }

  function fragmentWorldBounds(model, dbId) {
    var tree = model.getInstanceTree();
    var frags = model.getFragmentList();
    if (!tree || !frags) return null;

    var ids = [];
    tree.enumNodeFragments(dbId, function (fragId) { ids.push(fragId); }, true);
    if (ids.length === 0) return null;

    var box = new THREE.Box3();
    box.makeEmpty();
    for (var i = 0; i < ids.length; i++) {
      var b = new THREE.Box3();
      frags.getWorldBounds(ids[i], b);
      box.union(b);
    }
    return boxIsUsable(box) ? box : null;
  }

  // ------------------------------------------------- РАНТАЙМ-КАЛИБРОВКА

  /*
   * Определяет, в каком фрейме ACC хранит details.position.
   *
   * Берём пины, созданные НЕ нами — свои вызывающий код исключает по
   * списку issueId. У каждого известен элемент, значит известен и его
   * габарит в координатах Viewer. Проверка:
   *
   *   хранится viewer-local -> сама точка лежит у габарита
   *   хранится model frame  -> у габарита лежит (точка - globalOffset)
   *
   * |globalOffset| — десятки метров, габариты элементов — метры, поэтому
   * варианты не путаются. Требуем: победитель близко, проигравший далеко.
   *
   * referencePins: [{ position:{x,y,z}, objectId:<int>, externalId:<string> }]
   *
   * externalId предпочтительнее objectId. dbId присваивается при трансляции
   * и не переживает перепубликацию: пин, созданный против прошлой версии,
   * укажет на посторонний элемент, и калибровка проголосует за неверный
   * фрейм, не сообщив об этом. externalId устойчив.
   */
  function detectFrame(viewer, referencePins, externalIdMap) {
    var model = viewer.model;
    var g = vec(model.getData().globalOffset);
    var unit = feetPerUnit(model);

    var NEAR = 2.0 / unit;     // ~0.6 м: считаем попаданием
    var FAR = 10.0 / unit;     // ~3 м: проигравший должен быть дальше

    var votes = { ViewerLocal: 0, Model: 0 };
    var examined = 0;
    var resolvedByExternalId = 0;

    (referencePins || []).forEach(function (pin) {
      if (!pin || !pin.position) return;

      var dbId = null;
      if (externalIdMap && pin.externalId) {
        dbId = resolveDbId(model, externalIdMap, pin.externalId);
        if (dbId !== null && dbId !== undefined) resolvedByExternalId++;
      }
      if ((dbId === null || dbId === undefined) && pin.objectId !== undefined)
        dbId = pin.objectId;
      if (dbId === null || dbId === undefined) return;

      var box = fragmentWorldBounds(model, dbId);
      if (!box) return;

      var p = vec(pin.position);
      var dViewer = distanceToBox(p, box);
      var dModel = distanceToBox(p.clone().sub(g), box);

      examined++;

      if (dViewer < NEAR && dModel > FAR) votes.ViewerLocal++;
      else if (dModel < NEAR && dViewer > FAR) votes.Model++;
    });

    if (votes.ViewerLocal === 0 && votes.Model === 0) {
      return {
        frame: null,
        confidence: 0,
        votes: votes,
        examined: examined,
        resolvedByExternalId: resolvedByExternalId,
        note: 'Calibration failed: none of the ' + examined + ' reference pushpins ' +
              'produced an unambiguous result. Place a pushpin manually in ACC ' +
              'on an element from the current view and try again.'
      };
    }

    var winner = votes.Model > votes.ViewerLocal ? FRAME.MODEL : FRAME.VIEWER;
    var total = votes.ViewerLocal + votes.Model;
    var top = Math.max(votes.ViewerLocal, votes.Model);

    return {
      frame: winner,
      confidence: top / total,
      votes: votes,
      examined: examined,
      resolvedByExternalId: resolvedByExternalId,
      note: 'Calibration used ' + total + ' reference pushpins out of ' + examined +
            ' examined; resolved by externalId: ' + resolvedByExternalId
    };
  }

  // ------------------------------------------------- разрешение элемента

  function buildExternalIdMap(model) {
    return new Promise(function (resolve, reject) {
      model.getExternalIdMapping(resolve, reject);
    });
  }

  /*
   * externalId связанного элемента композитный: "<linkUid>/<elementUid>".
   * Прямой ключ может отдать узел без геометрии — в логе 7.0.12 это
   * кандидат с fragmentCount 0. Берём тот dbId, у которого фрагменты есть.
   */
  function resolveDbId(model, map, externalId) {
    var candidates = [];
    if (map[externalId] !== undefined) candidates.push(map[externalId]);

    var slash = externalId.lastIndexOf('/');
    if (slash > 0) {
      var tail = externalId.substring(slash + 1);
      if (map[tail] !== undefined) candidates.push(map[tail]);
    }

    for (var i = 0; i < candidates.length; i++) {
      if (fragmentWorldBounds(model, candidates[i])) return candidates[i];
    }
    return candidates.length ? candidates[0] : null;
  }

  // -------------------------------------------------------------- якорь

  // Не смотреть строго сверху и не смотреть в торец плиты.
  function approachDir(box) {
    var s = box.getSize(new THREE.Vector3());
    var d = (s.z < s.x && s.z < s.y)
      ? new THREE.Vector3(0.3, 0.5, 0.81)   // плоский элемент — сверху-сбоку
      : new THREE.Vector3(0.6, 0.6, 0.53);  // вытянутый — сбоку
    return d.normalize();
  }

  /*
   * Воспроизводим ровно то, что делает ручной пин ACC: наводим камеру на
   * элемент и стреляем hitTest в центр канвы. Элемент изолируем, чтобы
   * ничего не перекрыло.
   *
   * Асинхронно намеренно. hitTest считает луч по текущим матрицам камеры,
   * а setView и isolate вступают в силу только после перерисовки. Синхронный
   * выстрел сразу после setView попадает по старой камере: hit.dbId не
   * совпадает с целью, функция возвращает null, и КАЖДЫЙ элемент молча
   * уходит в деградированный BBox-якорь. Отказ был бы незаметен — качество
   * Snapped просто никогда бы не появилось.
   */
  function anchorByHitTest(viewer, dbId, box, offsetUnits) {
    var centre = box.getCenter(new THREE.Vector3());
    var diag = box.getSize(new THREE.Vector3()).length();
    var dir = approachDir(box);
    var dist = Math.min(Math.max(diag * 1.5, 10), 200);
    var eye = centre.clone().addScaledVector(dir, dist);

    viewer.isolate([dbId]);
    viewer.navigation.setCameraUpVector(new THREE.Vector3(0, 0, 1));
    viewer.navigation.setView(eye, centre);
    viewer.impl.invalidate(true, true, true);

    return nextFrame().then(function () { return delay(40); }).then(function () {
      // Канва, а не container: контейнер включает тулбар и панели, поэтому
      // его центр не совпадает с центром вида.
      var canvas = viewer.impl && viewer.impl.canvas ? viewer.impl.canvas : viewer.container;
      var r = canvas.getBoundingClientRect();
      var hit = viewer.impl.hitTest(r.width / 2, r.height / 2, true);

      if (!hit || hit.dbId !== dbId || !hit.intersectPoint) return null;

      /*
       * Отступ наружу. Направление "точка -> камера" гарантированно выводит
       * пин из тела и всегда доступно. Нормаль грани берём, только если
       * hitTest её реально вернул и она смотрит в ту же полусферу.
       */
      var away = eye.clone().sub(hit.intersectPoint).normalize();
      var usedNormal = false;

      if (hit.face && hit.face.normal) {
        var n = hit.face.normal.clone().normalize();
        if (n.dot(away) > 0) { away = n; usedNormal = true; }
      }

      return {
        quality: QUALITY.SNAPPED,
        point: hit.intersectPoint.clone().addScaledVector(away, offsetUnits),
        approach: dir,
        distance: dist,
        usedFaceNormal: usedNormal
      };
    });
  }

  // ------------------------------------------------------------- камера

  /*
   * Камера отдаётся в ОБОИХ фреймах.
   *
   * Это то место, где ломался билд 7.0.12: position уезжала в модельный
   * фрейм, а viewport оставался viewer-local, и они расходились ровно на
   * globalOffset — около 57 м на проекте Eilat. Вернуть камеру в одном
   * фрейме и понадеяться, что C# подберёт нужный, — тот же дефект. Поэтому
   * обе версии считаются здесь, из одной и той же точки, и вызывающий код
   * берёт пару целиком.
   */
  function deriveCameras(anchor, approach, distance, g) {
    var eye = anchor.clone().addScaledVector(approach, distance);
    function build(a, e) {
      var dir = a.clone().sub(e).normalize();
      var worldUp = new THREE.Vector3(0, 0, 1);
      if (Math.abs(dir.dot(worldUp)) > 0.999) worldUp.set(0, 1, 0);
      var right = new THREE.Vector3().crossVectors(dir, worldUp).normalize();
      var up = new THREE.Vector3().crossVectors(right, dir).normalize();
      return {
        eye: [e.x, e.y, e.z],
        target: [a.x, a.y, a.z],
        pivotPoint: [a.x, a.y, a.z],
        up: [up.x, up.y, up.z],
        worldUpVector: [0, 0, 1],
        distanceToOrbit: distance
      };
    }
    return {
      viewer: build(out(anchor), out(eye)),
      model: build(shift(anchor, g, 1), shift(eye, g, 1))
    };
  }

  // --------------------------------------------------------------- main

  function probeOne(viewer, model, map, req, g, offsetUnits) {
    var res = {
      key: req.key,
      externalId: req.externalId,
      ok: false,
      quality: QUALITY.NOTFOUND,
      globalOffset: out(g)
    };

    var dbId = resolveDbId(model, map, req.externalId);
    if (dbId === null || dbId === undefined) {
      res.note = 'The externalId did not resolve to a dbId in the current derivative';
      return Promise.resolve(res);
    }
    res.objectId = dbId;

    var box = fragmentWorldBounds(model, dbId);
    if (!box) {
      res.note = 'The dbId has no geometry fragments in the loaded viewable';
      return Promise.resolve(res);
    }
    res.bbox = { min: box.min.toArray(), max: box.max.toArray() };

    return anchorByHitTest(viewer, dbId, box, offsetUnits).then(function (a) {
      if (!a) {
        var dir = approachDir(box);
        var diag = box.getSize(new THREE.Vector3()).length();
        a = {
          quality: QUALITY.BBOX,
          point: box.getCenter(new THREE.Vector3()),
          approach: dir,
          distance: Math.min(Math.max(diag * 1.5, 10), 200),
          usedFaceNormal: false
        };
        res.note = 'hitTest missed the element; a degraded anchor was used and ' +
                   'pushpin accuracy is not guaranteed';
      }

      var cameras = deriveCameras(a.point, a.approach, a.distance, g);
      res.ok = true;
      res.quality = a.quality;
      res.usedFaceNormal = a.usedFaceNormal;
      res.positionViewer = out(a.point);
      res.positionModel = shift(a.point, g, 1);
      res.cameraViewer = cameras.viewer;
      res.cameraModel = cameras.model;
      return res;
    });
  }

  /*
   * Модель и нужный viewable должны быть загружены вызывающим кодом.
   * По каждому Issue возвращаются ОБА фрейма — и позиция, и камера, —
   * чтобы C# взял согласованную пару по одному значению из detectFrame.
   *
   * Запросы обрабатываются последовательно: каждый двигает камеру и
   * изоляцию, параллельный запуск перетирал бы состояние соседнего.
   */
  function probeBatch(viewer, payload) {
    var model = viewer.model;
    var g = vec(model.getData().globalOffset);
    var offsetUnits = PIN_OFFSET_FT / feetPerUnit(model);

    var savedState = viewer.getState({ viewport: true });
    var savedIsolation = viewer.getIsolatedNodes ? viewer.getIsolatedNodes() : [];

    return buildExternalIdMap(model).then(function (map) {
      var results = [];
      var chain = Promise.resolve();

      (payload.requests || []).forEach(function (req) {
        chain = chain.then(function () {
          return probeOne(viewer, model, map, req, g, offsetUnits)
            .then(function (r) { results.push(r); })
            .catch(function (e) {
              results.push({
                key: req.key,
                externalId: req.externalId,
                ok: false,
                quality: QUALITY.NOTFOUND,
                note: 'Probe exception: ' + (e && e.message ? e.message : e)
              });
            });
        });
      });

      return chain.then(function () {
        try {
          viewer.isolate(savedIsolation && savedIsolation.length ? savedIsolation : []);
          viewer.restoreState(savedState);
        } catch (e) { /* восстановление вида не критично для результата */ }
        return results;
      });
    });
  }

  global.BuildAIAnchorProbe = {
    QUALITY: QUALITY,
    FRAME: FRAME,
    detectFrame: detectFrame,
    probeBatch: probeBatch,
    fragmentWorldBounds: fragmentWorldBounds,
    resolveDbId: resolveDbId,
    buildExternalIdMap: buildExternalIdMap
  };

})(window);
