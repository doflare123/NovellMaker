local live_badge
return {
  on_load = function()
    ui.set_text(refs.title, "Меню проекта-примера")
    live_badge = ui.create(refs.badge, refs.panel)
    ui.set_visible(live_badge, true)
    ui.set_geometry(live_badge, {rotation=-5, opacity=0.8, color='#80CCFF'})
    ui.set_text(live_badge, "Живой экземпляр · ui.create")
  end,
  on_event = function(event)
    if event.node == refs.resume then ui.remove(live_badge); ui.close() end
  end
}