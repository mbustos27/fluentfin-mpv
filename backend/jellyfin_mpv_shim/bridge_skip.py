"""1.6 skip: the skip rules over the bridge (docs/BRIDGE.md section 4
``set_skip_options``' 1.6 fields, ``skip_rules``, ``add_skip_rule``,
``forget_skip_rules``, ``answer_skip_suggestion``; section 5
``skip_suggestion``).

Kept out of ``bridge.py`` so the protocol's skip half reads in one place:
``Bridge`` routes the four commands here and calls `account_connected`,
`apply_skip_options` and `suggestion_events` at their seams. The store is
`skip_rules.store()` (``skip-rules.json``, the backend its only writer).
"""

import logging

from . import bridge_protocol as proto
from . import skip_rules
from .bridge_protocol import NO_PLAYBACK, NOT_CONNECTED, BridgeError

log = logging.getLogger("bridge_skip")


def account_connected(server):
    """``connect`` succeeded (``server`` is `validate_connect`'s): rules and
    learning are this account's from now on; 1.4's learned intros migrate
    to it if they have not yet."""
    key = None
    try:
        key = skip_rules.account_key(server.get("serverId"),
                                     server.get("userId"), server.get("url"))
    except Exception:
        log.debug("no account for the skip rules", exc_info=True)
    skip_rules.set_account(key)
    if key:
        try:
            skip_rules.store().migrate(key)
        except Exception:
            log.warning("could not migrate the learned intros",
                        exc_info=True)


def account_cleared():
    """No connection: nothing is learned for anyone."""
    skip_rules.set_account(None)


def apply_skip_options(options):
    """The 1.6 fields of a validated ``set_skip_options``: the segment types'
    actions (held in memory, as ``set_auto_play`` holds its own: a save of
    any other setting writes the user's own values, `conf.SESSION_ONLY`)
    and what a rule with no choice of its own does."""
    from . import conf

    actions = options.get("segmentActions")
    for kind, action in (actions or {}).items():
        key = conf.SEGMENT_SETTINGS.get(kind)
        if key is None:
            continue
        conf.SESSION_ONLY.setdefault(key, getattr(conf.settings, key))
        setattr(conf.settings, key, action)
    if options.get("ruleAction") is not None:
        skip_rules.OPTIONS.rule_action = options["ruleAction"]


def _account_for(bridge, args):
    """The account a list or forget is about: the connected one, or before
    ``connect`` the ``serverId`` and ``userId`` given."""
    given = proto.validate_skip_rules_account(args)
    if given is not None:
        return skip_rules.account_key(given["serverId"], given["userId"])
    if not bridge.connected:
        raise BridgeError(NOT_CONNECTED, "send connect first, or name the "
                                         "serverId and userId")
    account = skip_rules.current_account()
    if account is None:
        raise BridgeError(NOT_CONNECTED, "send connect first")
    return account


def cmd_skip_rules(bridge, args):
    """The account's shows with their rules, learned intros and refusal."""
    account = _account_for(bridge, args)
    return skip_rules.listing(skip_rules.store(), account)


def _playing_item(bridge):
    pm = bridge.runtime.pm
    video = bridge._playing(pm) if pm is not None else None
    return video, (getattr(video, "item", None) or {}) if video else {}


def cmd_add_skip_rule(bridge, args):
    """A rule the viewer marked (the player's "Skip a part every episode")
    for ``seriesId``, or the show playing. Applies to the episode playing
    too (`refresh_rules`)."""
    if not bridge.connected:
        raise BridgeError(NOT_CONNECTED, "send connect first")
    request = proto.validate_add_skip_rule(args)
    account = skip_rules.current_account()
    if account is None:
        raise BridgeError(NOT_CONNECTED, "send connect first")
    video, item = _playing_item(bridge)
    series = request["seriesId"]
    name = request["seriesName"]
    if series is None:
        series = skip_rules.series_id(item)
        if series is None:
            raise BridgeError(NO_PLAYBACK, "no episode of a show is playing")
    if name is None and skip_rules.series_id(item) == series:
        name = item.get("SeriesName") if isinstance(
            item.get("SeriesName"), str) else None
    key = "%s:%s" % (account, series)
    try:
        rule_id = skip_rules.store().add_rule(
            key, request["anchor"], request["startS"], request["endS"],
            request["action"], "marked", name)
    except ValueError as error:
        raise BridgeError(proto.BAD_ARGS, str(error))
    log.info("Skip rule %s added (%s, %.1f-%.1f s)", rule_id,
             request["anchor"], request["startS"], request["endS"])
    refresh_rules(bridge, series)
    return {"ruleId": rule_id}


def cmd_forget_skip_rules(bridge, args):
    """Forgets one rule, one show's, or (neither given) all of the
    account's."""
    account = _account_for(bridge, args)
    request = proto.validate_forget_skip_rules(args)
    changed = skip_rules.store().forget(account, request["seriesId"],
                                        request["ruleId"])
    if changed:
        refresh_rules(bridge, request["seriesId"])
    return {"forgotten": changed}


def cmd_answer_skip_suggestion(bridge, args):
    """The viewer's answer to a ``skip_suggestion``: ``yes`` (a rule, which
    applies to the episode playing too), ``not_now``, ``never`` (no more
    suggestions for that show). ``bad_args`` for a suggestion not known (any
    more)."""
    if not bridge.connected:
        raise BridgeError(NOT_CONNECTED, "send connect first")
    request = proto.validate_skip_suggestion_answer(args)
    account = skip_rules.current_account()
    if account is None:
        raise BridgeError(NOT_CONNECTED, "send connect first")
    try:
        bridge.learner.answered(request["id"])
    except Exception:
        log.debug("the watcher did not take the answer", exc_info=True)
    result = skip_rules.store().answer(account, request["id"],
                                       request["answer"], request["action"])
    if result is None:
        raise BridgeError(proto.BAD_ARGS, "no such skip suggestion")
    if result.get("ruleId"):
        refresh_rules(bridge, None)
    return result


def refresh_rules(bridge, series):
    """The episode playing gets its show's rules again (after one was added
    or forgotten), on the action thread; the next ``segments`` shows them.
    ``series`` None: whatever show is playing."""
    pm = bridge.runtime.pm
    video = bridge._playing(pm) if pm is not None else None
    if video is None:
        return
    item = getattr(video, "item", None) or {}
    if series is not None and skip_rules.series_id(item) != series:
        return

    def run():
        if pm._video is not video:
            return
        try:
            from .media import Intro

            key = skip_rules.show_key(item)
            before = {getattr(s, "rule_id", None): s for s in video.intros
                      if getattr(s, "source", None) == skip_rules.SOURCE_RULE}
            kept = [s for s in video.intros
                    if getattr(s, "source", None) != skip_rules.SOURCE_RULE]
            video.intros[:] = kept
            if key is not None:
                skip_rules.add_rules(video, Intro, key, skip_rules.store())
                # 1.6.1: what this episode already did with each rule (an
                # "always" part skipped stays skipped: no re-arm on a Yes
                # or an Add mid-episode)
                try:
                    position = pm._player.playback_time
                except Exception:
                    position = None
                for segment in video.intros:
                    if getattr(segment, "source", None) == \
                            skip_rules.SOURCE_RULE:
                        skip_rules.carry_rule_state(
                            before.get(segment.rule_id), segment, position)
        except Exception:
            log.debug("could not refresh the skip rules", exc_info=True)

    try:
        pm.put_task(run)
        bridge.poke()
    except Exception:
        log.debug("could not queue the skip rules' refresh", exc_info=True)


def suggestion_events(watcher):
    """``[("skip_suggestion", data)]`` for what the watcher found."""
    out = []
    for suggestion in watcher.take_suggestions():
        try:
            out.append(("skip_suggestion",
                        proto.skip_suggestion_payload(suggestion)))
        except Exception:
            log.debug("unusable skip suggestion", exc_info=True)
    return out


def segment_extra(segment):
    """What a rule's segment adds to the ``segments`` event, else None."""
    if getattr(segment, "source", None) != skip_rules.SOURCE_RULE:
        return None
    return {"ruleId": getattr(segment, "rule_id", None),
            "origin": getattr(segment, "origin", None),
            "anchor": getattr(segment, "anchor", None)}
