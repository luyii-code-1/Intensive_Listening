import { defineConfig } from 'vitepress';

export default defineConfig({
  lang: 'zh-CN',
  title: 'Intensive Listening 文档',
  description: '从打开第一份精听课程，到播放录音、制作字幕和分享课程。',
  base: '/docs/',
  head: [['link', { rel: 'icon', href: '/assets/logo.svg' }]],
  themeConfig: {
    logo: 'https://il.luyii.cn/assets/logo.svg',
    siteTitle: 'Intensive Listening',
    nav: [
      { text: '使用文档', link: '/' },
      { text: '下载应用', link: 'https://il.luyii.cn/#download' },
      { text: 'Web 播放器', link: 'https://web.il.luyii.cn/' },
      { text: '官网', link: 'https://il.luyii.cn/' }
    ],
    sidebar: [
      { text: '开始使用', items: [
        { text: '文档首页', link: '/' },
        { text: '第一次用，从这里开始', link: '/getting-started' },
        { text: '安装桌面应用', link: '/installation' },
        { text: '完成首次设置', link: '/first-run' },
        { text: '体验 Web 示例课程', link: '/web-player' }
      ] },
      { text: '播放与练习', items: [
        { text: '打开课程，逐句精听', link: '/listening' },
        { text: '设置与快捷按键', link: '/settings' }
      ] },
      { text: '制作与分享', items: [
        { text: '选择和配置转写服务', link: '/transcription' },
        { text: '从音频制作课程', link: '/authoring' },
        { text: '使用智能体协助制作', link: '/agents' }
      ] },
      { text: '遇到问题', items: [{ text: '常见问题', link: '/faq' }] }
    ],
    search: {
      provider: 'local',
      options: {
        miniSearch: {
          options: {
            tokenize(text) {
              if (typeof Intl.Segmenter === 'function') {
                return Array.from(new Intl.Segmenter('zh-CN', { granularity: 'word' }).segment(text))
                  .filter(part => part.isWordLike).map(part => part.segment);
              }
              return text.match(/[\p{Script=Han}]|[\p{Letter}\p{Number}]+/gu) || [];
            }
          }
        },
        translations: {
          button: { buttonText: '搜索文档', buttonAriaLabel: '搜索文档' },
          modal: {
            displayDetails: '显示详细内容', resetButtonTitle: '清空搜索', backButtonTitle: '返回', noResultsText: '没有找到相关内容',
            footer: { selectText: '打开', navigateText: '切换', closeText: '关闭' }
          }
        }
      }
    },
    outline: { label: '本页目录', level: [2, 3] },
    docFooter: { prev: '上一篇', next: '下一篇' },
    returnToTopLabel: '回到顶部', sidebarMenuLabel: '文档目录',
    darkModeSwitchLabel: '外观', lightModeSwitchTitle: '切换为浅色', darkModeSwitchTitle: '切换为深色',
    notFound: { title: '没有找到这篇文档', quote: '从文档首页选择你想了解的内容。', linkLabel: '打开文档首页', linkText: '打开文档首页' }
  }
});
