import { createRouter, createWebHistory } from 'vue-router'
import DashboardView from './views/DashboardView.vue'
import UrlDetailView from './views/UrlDetailView.vue'
import AlertsView from './views/AlertsView.vue'

export default createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'dashboard', component: DashboardView },
    { path: '/urls/:id', name: 'url', component: UrlDetailView, props: (route) => ({ id: Number(route.params.id) }) },
    { path: '/alerts', name: 'alerts', component: AlertsView },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})
