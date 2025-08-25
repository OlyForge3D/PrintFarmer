(function(){
  function setColumnWidth(table, colIndex, width){
    const ths = table.querySelectorAll('thead th');
    if(!ths[colIndex]) return;
    ths[colIndex].style.width = width + 'px';
    const rows = table.tBodies[0] ? table.tBodies[0].rows : [];
    for(let r=0;r<rows.length;r++){
      const cell = rows[r].children[colIndex];
      if(cell){ cell.style.width = width + 'px'; }
    }
  }
  function getWidths(table){
    const ths = table.querySelectorAll('thead th');
    return Array.from(ths).map(th => th.getBoundingClientRect().width|0);
  }
  function saveWidths(table){
    const id = table.id || 'table';
    const key = 'colWidths:' + id;
    try{
      const widths = getWidths(table);
      localStorage.setItem(key, JSON.stringify(widths));
    }catch{}
  }
  function applySaved(tableId){
    try{
      const table = document.getElementById(tableId);
      if(!table) return;
      const key = 'colWidths:' + tableId;
      const raw = localStorage.getItem(key);
      if(!raw) return;
      const widths = JSON.parse(raw);
      if(!Array.isArray(widths)) return;
      for(let i=0;i<widths.length;i++){
        const w = widths[i];
        if(typeof w === 'number' && w>24) setColumnWidth(table, i, w);
      }
    }catch{}
  }
  function start(e, tableId, colIndex){
    e.preventDefault();
    e.stopPropagation();
    const table = document.getElementById(tableId);
    if(!table) return;
    const ths = table.querySelectorAll('thead th');
    const th = ths[colIndex];
    if(!th) return;
    const startX = e.pageX || (e.touches && e.touches[0] && e.touches[0].pageX) || 0;
    const startW = th.getBoundingClientRect().width;
    function onMove(ev){
      const x = (ev.pageX !== undefined) ? ev.pageX : (ev.touches && ev.touches[0] && ev.touches[0].pageX) || 0;
      const delta = x - startX;
      const newW = Math.max(48, startW + delta);
      setColumnWidth(table, colIndex, newW);
    }
    function onUp(){
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      document.removeEventListener('touchmove', onMove);
      document.removeEventListener('touchend', onUp);
      saveWidths(table);
    }
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
    document.addEventListener('touchmove', onMove, {passive:false});
    document.addEventListener('touchend', onUp, {passive:false});
  }
  window.columnResizer = { start, applySaved };
})();
