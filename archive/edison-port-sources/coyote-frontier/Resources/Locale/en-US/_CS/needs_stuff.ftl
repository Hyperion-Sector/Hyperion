examinable-need-header = [bold][underline][color={$color}]{$needname}[/color][/underline][/bold]

# Hunger
examinable-need-hunger-extrasatisfied  = {CAPITALIZE(SUBJECT($entity))} {CONJUGATE-BASIC($entity, "look", "looks")} stuffed!
examinable-need-hunger-satisfied       = {CAPITALIZE(SUBJECT($entity))} {CONJUGATE-BASIC($entity, "look", "looks")} content.
examinable-need-hunger-low             = {CAPITALIZE(SUBJECT($entity))} {CONJUGATE-BASIC($entity, "look", "looks")} hungry.
examinable-need-hunger-low-meme        = {CAPITALIZE(SUBJECT($entity))} {CONJUGATE-BASIC($entity, "look", "looks")} hungie.
examinable-need-hunger-critical        = {CAPITALIZE(SUBJECT($entity))} {CONJUGATE-BASIC($entity, "look", "looks")} starved!
examinable-need-hunger-unused          = {CAPITALIZE(SUBJECT($entity))} {CONJUGATE-BASIC($entity, "look", "looks")} like they dont actually get hungry!

examinable-need-hunger-extrasatisfied-self  = You feel nice and full!
examinable-need-hunger-satisfied-self       = You feel satisfied.
examinable-need-hunger-low-self             = You feel a bit hungry.
examinable-need-hunger-low-self-meme        = You feel a bit hungie.
examinable-need-hunger-critical-self        = You feel absolutely starving!
examinable-need-hunger-numberized           = You'd rate {CAPITALIZE(SUBJECT($entity))}'s satiation as {$current}/{$max}.
examinable-need-hunger-numberized-self      = You'd rate your satiation as {$current}/{$max}.

examinable-need-hunger-timeleft-extrasatisfied-self   = You feel you'll stay stuffed for around...
examinable-need-hunger-timeleft-satisfied-self        = You feel you'll stay satisfied for around...
examinable-need-hunger-timeleft-low-self              = You feel you could stand this hunger for around....
examinable-need-hunger-timeleft-low-meme-self         = You feel you could stand this hungieness for around....
examinable-need-hunger-timeleft-critical-self         = {CAPITALIZE($creature)} needs food badly!

examinable-need-hunger-timeleft-extrasatisfied = You feel {CAPITALIZE(SUBJECT($entity))} will remain stuffed for around...
examinable-need-hunger-timeleft-satisfied      = You feel {CAPITALIZE(SUBJECT($entity))} will remain satisfied for around....
examinable-need-hunger-timeleft-low            = You feel {CAPITALIZE(SUBJECT($entity))} could put up with their hunger for around...
examinable-need-hunger-timeleft-low-meme       = You feel {CAPITALIZE(SUBJECT($entity))} could put up with their hungieness for around...
examinable-need-hunger-timeleft-critical       = {CAPITALIZE($creature)} needs food badly!

examinable-need-hunger-timeleft-tillcritical = {CAPITALIZE($creature)} will starve in about...
examinable-need-hunger-timeleft-tillcritical-self = You will starve in about...

# Thirst
examinable-need-thirst-extrasatisfied  = {CAPITALIZE(SUBJECT($entity))} {CONJUGATE-BASIC($entity, "look", "looks")} well hydrated!
examinable-need-thirst-satisfied       = {CAPITALIZE(SUBJECT($entity))} {CONJUGATE-BASIC($entity, "look", "looks")} quenched.
examinable-need-thirst-low             = {CAPITALIZE(SUBJECT($entity))} {CONJUGATE-BASIC($entity, "look", "looks")} thirsty.
examinable-need-thirst-low-meme        = {CAPITALIZE(SUBJECT($entity))} {CONJUGATE-BASIC($entity, "look", "looks")} thurrsty.
examinable-need-thirst-critical        = {CAPITALIZE(SUBJECT($entity))} {CONJUGATE-BASIC($entity, "look", "looks")} dehydrated!
examinable-need-thirst-unused          = {CAPITALIZE(SUBJECT($entity))} {CONJUGATE-BASIC($entity, "look", "looks")} like they dont actually get thirsty!

examinable-need-thirst-extrasatisfied-self  = You feel nice and hydrated!
examinable-need-thirst-satisfied-self       = You feel satisfied.
examinable-need-thirst-low-self             = You feel a bit thirsty.
examinable-need-thirst-low-self-meme        = You feel a bit thurrsty.
examinable-need-thirst-critical-self        = You feel absolutely parched!
examinable-need-thirst-numberized           = You'd rate {CAPITALIZE(SUBJECT($entity))}'s hydration as {$current}/{$max}.
examinable-need-thirst-numberized-self      = You'd rate your hydration as {$current}/{$max}.

examinable-need-thirst-timeleft-extrasatisfied-self  = You feel like you'll stay hydrated for around...
examinable-need-thirst-timeleft-satisfied-self       = You feel like you'll stay satisfied for around...
examinable-need-thirst-timeleft-low-self             = You feel like you could stand this thirst for around...
examinable-need-thirst-timeleft-low-meme-self        = You feel like you could stand this thurrstyness for around...
examinable-need-thirst-timeleft-critical-self        = {CAPITALIZE($creature)} needs water badly!

examinable-need-thirst-timeleft-extrasatisfied  = You feel like {CAPITALIZE(SUBJECT($entity))} will remain hydrated for around...
examinable-need-thirst-timeleft-satisfied       = You feel like {CAPITALIZE(SUBJECT($entity))} will remain satisfied for around...
examinable-need-thirst-timeleft-low             = You feel like {CAPITALIZE(SUBJECT($entity))} could put up with their thirst for around...
examinable-need-thirst-timeleft-low-meme        = You feel like {CAPITALIZE(SUBJECT($entity))} could put up with their thurrstyness for around...
examinable-need-thirst-timeleft-critical        = {CAPITALIZE($creature)} needs water badly!

examinable-need-thirst-timeleft-tillcritical = {CAPITALIZE($creature)} will dehydrate in about...
examinable-need-thirst-timeleft-tillcritical-self = You will dehydrate in about...

# Buffs and Debuffs
examinable-need-effect-header = [bold][underline]Effects:[/underline][/bold]
examinable-need-effect-buff = [color=green]{$amount}[/color] {$kind}
examinable-need-effect-debuff = [color=red]{$amount}[/color] {$kind}
examinable-need-effect-buff-custom = [color=green]{$kind}[/color] {$text}
examinable-need-effect-debuff-custom = [color=red]{$kind}[/color] {$text}

examinable-need-verb-text = Needs
examinable-need-verb-disabled = You can't quite make out their needs from here, maybe get closer?
examinable-need-verb-no-id = You can't quite make out their needs from here, maybe get closer?


need-slowdown-hunger-start = You're so hungry, it's hard to move...
need-slowdown-hunger-end = You regain some strength, though you're still hungry.
need-slowdown-thirst-start = You're so thirsty, it's hard to move...
need-slowdown-thirst-end = You regain some strength, though you're still thirsty.
need-slowdown-default-start = You're feeling weak, it's hard to move...
need-slowdown-default-end = You regain some strength.
